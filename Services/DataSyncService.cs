using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using static WorkerService1.Services.ActivityDatabaseService;

namespace WorkerService1.Services;

public class DataSyncService
{
    private readonly ILogger<DataSyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ActivityDatabaseService _activityDatabaseService;
    private readonly string _sqlitePath;
    private readonly string _sqliteConnectionString;
    private readonly Timer _syncTimer;
    private readonly SemaphoreSlim _syncLock;

    private string _mysqlHost = string.Empty;
    private int _mysqlPort = 3306;
    private string _mysqlDatabase = string.Empty;
    private string _mysqlUser = string.Empty;
    private string _mysqlPassword = string.Empty;
    private string _mysqlConnectionString = string.Empty;

    private bool _enabled = true;
    private int _syncIntervalMinutes = 10;
    private int _batchSize = 100;
    private bool _deleteAfterSync = false;

    public DataSyncService(
        ILogger<DataSyncService> logger,
        IConfiguration configuration,
        ConfigurationService configService,
        ActivityDatabaseService activityDatabaseService)
    {
        _logger = logger;
        _configuration = configuration;
        _activityDatabaseService = activityDatabaseService;
        _syncLock = new SemaphoreSlim(1, 1);

        var dataDir = configService.GetSafeDataDirectory(_configuration["PerformanceAgent:DataDirectory"] ?? "");
        _sqlitePath = Path.Combine(dataDir, "activities_v2.db");
        _sqliteConnectionString = $"Data Source={_sqlitePath}";

        LoadConfiguration();
        BuildMysqlConnectionString();

        if (_enabled && !string.IsNullOrEmpty(_mysqlHost))
        {
            var intervalMs = _syncIntervalMinutes * 60 * 1000;
            _syncTimer = new Timer(SyncCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(intervalMs));
            _logger.LogInformation("DataSyncService initialized - MySQL sync enabled (Interval: {Minutes} min)", _syncIntervalMinutes);
        }
        else
        {
            _syncTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
            _logger.LogWarning("DataSyncService disabled or MySQL not configured");
        }
    }

    private void LoadConfiguration()
    {
        _enabled = _configuration.GetValue<bool>("DataSync:Enabled", true);
        _syncIntervalMinutes = _configuration.GetValue<int>("DataSync:SyncIntervalMinutes", 10);
        _batchSize = _configuration.GetValue<int>("DataSync:BatchSize", 100);
        _deleteAfterSync = _configuration.GetValue<bool>("DataSync:DeleteAfterSync", false);

        _mysqlHost = _configuration.GetValue<string>("DataSync:MySqlHost") ?? "";
        _mysqlPort = _configuration.GetValue<int>("DataSync:MySqlPort", 3306);
        _mysqlDatabase = _configuration.GetValue<string>("DataSync:MySqlDatabase") ?? "";
        _mysqlUser = _configuration.GetValue<string>("DataSync:MySqlUser") ?? "";
        _mysqlPassword = _configuration.GetValue<string>("DataSync:MySqlPassword") ?? "";
    }

    private void BuildMysqlConnectionString()
    {
        if (string.IsNullOrEmpty(_mysqlHost)) return;

        _mysqlConnectionString = $"Server={_mysqlHost};Port={_mysqlPort};Database={_mysqlDatabase};" +
                                 $"Uid={_mysqlUser};Pwd={_mysqlPassword};SslMode=none;CharSet=utf8mb4;";
    }

    private async void SyncCallback(object? state)
    {
        try
        {
            await SyncDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in sync callback");
        }
    }

    public async Task SyncDataAsync()
    {
        if (!_enabled || string.IsNullOrEmpty(_mysqlHost))
        {
            _logger.LogDebug("Sync disabled or MySQL not configured");
            return;
        }

        await _syncLock.WaitAsync();
        try
        {
            if (!await TestMysqlConnectionAsync())
            {
                _logger.LogWarning("MySQL connection failed - skipping sync");
                return;
            }

            // Quick check: any data to sync?
            var hasUnsyncedData = await HasUnsyncedDataAsync();
            if (!hasUnsyncedData)
            {
                _logger.LogDebug("No unsynced data - skipping sync");
                return;
            }

            // Sync activities
            var activities = await GetUnsyncedActivitiesAsync();

            if (activities.Count > 0)
            {
                _logger.LogInformation("Syncing {Count} activities to MySQL", activities.Count);

                var insertedCount = await InsertToMysqlAsync(activities);

                if (insertedCount > 0)
                {
                    await MarkAsSyncedAsync([.. activities.Select(a => a.Id)]);
                    _logger.LogInformation("Successfully synced {Count} activities to MySQL", insertedCount);

                    if (_deleteAfterSync)
                    {
                        await DeleteSyncedActivitiesAsync([.. activities.Select(a => a.Id)]);
                        _logger.LogInformation("Deleted {Count} synced activities from SQLite", insertedCount);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No new activities to sync");
            }

            // Sync browser data
            var browserData = await GetUnsyncedBrowserDataAsync();

            if (browserData.Count > 0)
            {
                _logger.LogInformation("Syncing {Count} browser data entries to MySQL", browserData.Count);

                var insertedBrowserCount = await InsertBrowserDataToMysqlAsync(browserData);

                if (insertedBrowserCount > 0)
                {
                    await MarkBrowserDataAsSyncedAsync([.. browserData.Select(b => b.Id)]);
                    _logger.LogInformation("Successfully synced {Count} browser data entries to MySQL", insertedBrowserCount);

                    if (_deleteAfterSync)
                    {
                        await DeleteSyncedBrowserDataAsync([.. browserData.Select(b => b.Id)]);
                        _logger.LogInformation("Deleted {Count} synced browser data entries from SQLite", insertedBrowserCount);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No new browser data to sync");
            }

            // Sync installed apps
            var installedApps = await _activityDatabaseService.GetUnsyncedInstalledAppsAsync(_batchSize);

            if (installedApps.Count > 0)
            {
                _logger.LogInformation("Syncing {Count} installed apps to MySQL", installedApps.Count);

                var insertedAppsCount = await InsertInstalledAppsToMysqlAsync(installedApps);

                if (insertedAppsCount > 0)
                {
                    await MarkInstalledAppsAsSyncedAsync([.. installedApps.Select(a => a.Id)]);
                    _logger.LogInformation("Successfully synced {Count} installed apps to MySQL", insertedAppsCount);

                    if (_deleteAfterSync)
                    {
                        await DeleteSyncedInstalledAppsAsync([.. installedApps.Select(a => a.Id)]);
                        _logger.LogInformation("Deleted {Count} synced installed apps from SQLite", insertedAppsCount);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No new installed apps to sync");
            }

            // Sync system hardware
            var hardwareData = await _activityDatabaseService.GetUnsyncedSystemHardwareAsync(_batchSize);

            if (hardwareData.Count > 0)
            {
                _logger.LogInformation("Syncing {Count} system hardware entries to MySQL", hardwareData.Count);

                var insertedHardwareCount = await InsertSystemHardwareToMysqlAsync(hardwareData);

                if (insertedHardwareCount > 0)
                {
                    await MarkSystemHardwareAsSyncedAsync([.. hardwareData.Select(h => h.Id)]);
                    _logger.LogInformation("Successfully synced {Count} system hardware entries to MySQL", insertedHardwareCount);

                    if (_deleteAfterSync)
                    {
                        await DeleteSyncedSystemHardwareAsync([.. hardwareData.Select(h => h.Id)]);
                        _logger.LogInformation("Deleted {Count} synced system hardware entries from SQLite", insertedHardwareCount);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No new system hardware to sync");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during data sync");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Quick check if there's any unsynced data (activities or browser data)
    /// </summary>
    private async Task<bool> HasUnsyncedDataAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    (SELECT COUNT(*) FROM ActivityLogs WHERE Synced = 0 OR Synced IS NULL) +
                    (SELECT COUNT(*) FROM BrowserDatas WHERE Synced = 0 OR Synced IS NULL) AS Total";

            using var command = new SqliteCommand(sql, connection);
            var total = Convert.ToInt32(await command.ExecuteScalarAsync());

            return total > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for unsynced data");
            return true; // On error, assume there's data to be safe
        }
    }

    private async Task<bool> TestMysqlConnectionAsync()
    {
        try
        {
            using var conn = new MySqlConnection(_mysqlConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MySQL connection test failed");
            return false;
        }
    }

    private async Task<List<ActivityLog>> GetUnsyncedActivitiesAsync()
    {
        var activities = new List<ActivityLog>();

        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, Evt, ActivityType, ProcessName, Title, StartTimeUtc, EndTimeUtc, 
                       DurationMs, Username, Domain, UserSid, MotherboardUuid, Url, Browser, CreatedAtUtc
                FROM ActivityLogs 
                WHERE Synced = 0 OR Synced IS NULL
                ORDER BY StartTimeUtc ASC
                LIMIT @BatchSize";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@BatchSize", _batchSize);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                activities.Add(new ActivityLog
                {
                    Id = reader.GetInt32(0),
                    Evt = reader.GetString(1),
                    ActivityType = reader.GetString(2),
                    ProcessName = reader.GetString(3),
                    Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                    StartTimeUtc = DateTime.Parse(reader.GetString(5)),
                    EndTimeUtc = DateTime.Parse(reader.GetString(6)),
                    DurationMs = reader.GetInt64(7),
                    Username = reader.GetString(8),
                    Domain = reader.GetString(9),
                    UserSid = reader.GetString(10),
                    MotherboardUuid = reader.GetString(11),
                    Url = reader.IsDBNull(12) ? null : reader.GetString(12),
                    Browser = reader.IsDBNull(13) ? null : reader.GetString(13),
                    CreatedAtUtc = DateTime.Parse(reader.GetString(14))
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading unsynced activities from SQLite");
        }

        return activities;
    }

    private async Task<int> InsertToMysqlAsync(List<ActivityLog> activities)
    {
        int insertedCount = 0;

        try
        {
            using var conn = new MySqlConnection(_mysqlConnectionString);
            await conn.OpenAsync();

            await CreateMysqlTableIfNotExistsAsync(conn);

            using var transaction = await conn.BeginTransactionAsync();

            var sql = @"INSERT INTO activities (
                            evt, activity_type, process_name, title,
                            start_time_utc, end_time_utc, duration_ms,
                            username, domain, user_sid, motherboard_uuid, url, browser, base_url, created_at_utc
                        ) VALUES (
                            @evt, @activity_type, @process_name, @title,
                            @start_time_utc, @end_time_utc, @duration_ms,
                            @username, @domain, @user_sid, @motherboard_uuid, @url, @browser, @base_url, @created_at_utc
                        )";

            foreach (var activity in activities)
            {
                try
                {
                    using var cmd = new MySqlCommand(sql, conn, transaction);

                    cmd.Parameters.AddWithValue("@evt", activity.Evt);
                    cmd.Parameters.AddWithValue("@activity_type", activity.ActivityType);
                    cmd.Parameters.AddWithValue("@process_name", activity.ProcessName);
                    cmd.Parameters.AddWithValue("@title", activity.Title ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@start_time_utc", activity.StartTimeUtc);
                    cmd.Parameters.AddWithValue("@end_time_utc", activity.EndTimeUtc);
                    cmd.Parameters.AddWithValue("@duration_ms", activity.DurationMs);
                    cmd.Parameters.AddWithValue("@username", activity.Username);
                    cmd.Parameters.AddWithValue("@domain", activity.Domain);
                    cmd.Parameters.AddWithValue("@user_sid", activity.UserSid);
                    cmd.Parameters.AddWithValue("@motherboard_uuid", activity.MotherboardUuid);
                    cmd.Parameters.AddWithValue("@url", activity.Url ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@browser", activity.Browser ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@base_url", activity.BaseUrl ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_at_utc", activity.CreatedAtUtc);

                    await cmd.ExecuteNonQueryAsync();
                    insertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error inserting activity {Id}", activity.Id);
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during MySQL insert");
        }

        return insertedCount;
    }

    private static async Task CreateMysqlTableIfNotExistsAsync(MySqlConnection conn)
    {
        // Activities table with base_url
        var createActivitiesTableSql = @"
            CREATE TABLE IF NOT EXISTS activities (
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                evt VARCHAR(50) NOT NULL DEFAULT 'activity',
                activity_type VARCHAR(50) NOT NULL DEFAULT 'window',
                process_name VARCHAR(255) NOT NULL,
                title TEXT NULL,
                start_time_utc DATETIME NOT NULL,
                end_time_utc DATETIME NOT NULL,
                duration_ms BIGINT NOT NULL,
                username VARCHAR(255) NOT NULL,
                domain VARCHAR(255) NOT NULL,
                user_sid VARCHAR(255) NOT NULL DEFAULT '',
                motherboard_uuid VARCHAR(255) NOT NULL DEFAULT '',
                url TEXT NULL,
                browser VARCHAR(100) NULL,
                base_url VARCHAR(512) NULL,
                created_at_utc DATETIME NOT NULL,
                received_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                
                INDEX idx_activity_type (activity_type),
                INDEX idx_process_name (process_name),
                INDEX idx_start_time (start_time_utc),
                INDEX idx_username (username),
                INDEX idx_browser (browser),
                INDEX idx_base_url (base_url),
                INDEX idx_user_sid (user_sid),
                INDEX idx_motherboard_uuid (motherboard_uuid)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

        using (var cmd = new MySqlCommand(createActivitiesTableSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Browser data table
        var createBrowserDataTableSql = @"
            CREATE TABLE IF NOT EXISTS browser_datas (
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NULL,
                browser VARCHAR(100) NOT NULL,
                visit_time_utc DATETIME NOT NULL,
                base_url VARCHAR(512) NULL,
                username VARCHAR(255) NOT NULL,
                domain VARCHAR(255) NOT NULL,
                user_sid VARCHAR(255) NOT NULL DEFAULT '',
                motherboard_uuid VARCHAR(255) NOT NULL DEFAULT '',
                created_at_utc DATETIME NOT NULL,
                received_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                
                INDEX idx_browser (browser),
                INDEX idx_visit_time (visit_time_utc),
                INDEX idx_base_url (base_url),
                INDEX idx_username (username),
                INDEX idx_user_sid (user_sid),
                INDEX idx_motherboard_uuid (motherboard_uuid)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

        using (var cmd = new MySqlCommand(createBrowserDataTableSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Installed apps table
        var createInstalledAppsTableSql = @"
            CREATE TABLE IF NOT EXISTS installed_apps (
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                app_name VARCHAR(500) NOT NULL,
                username VARCHAR(255) NOT NULL,
                domain VARCHAR(255) NOT NULL,
                user_sid VARCHAR(255) NOT NULL DEFAULT '',
                motherboard_uuid VARCHAR(255) NOT NULL DEFAULT '',
                machine_name VARCHAR(255),
                collected_at DATETIME NOT NULL,
                
                INDEX idx_username (username),
                INDEX idx_collected_at (collected_at),
                INDEX idx_machine_name (machine_name),
                INDEX idx_app_name (app_name(255)),
                INDEX idx_user_sid (user_sid),
                INDEX idx_motherboard_uuid (motherboard_uuid)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

        using (var cmd = new MySqlCommand(createInstalledAppsTableSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // System hardware table
        var createSystemHardwareTableSql = @"
            CREATE TABLE IF NOT EXISTS system_hardware (
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                hostname VARCHAR(255) NOT NULL,
                username VARCHAR(255) NOT NULL,
                domain VARCHAR(255) NOT NULL,
                user_sid VARCHAR(255) NOT NULL DEFAULT '',
                os_version VARCHAR(255),
                model VARCHAR(500),
                serial_number VARCHAR(255),
                motherboard_uuid VARCHAR(255),
                disk_total_gb DECIMAL(10,2),
                disk_used_gb DECIMAL(10,2),
                disk_free_gb DECIMAL(10,2),
                disk_usage_percent DECIMAL(5,2),
                ram_total_gb DECIMAL(10,2),
                ram_used_gb DECIMAL(10,2),
                ram_free_gb DECIMAL(10,2),
                ram_usage_percent DECIMAL(5,2),
                collected_at DATETIME NOT NULL,
                
                INDEX idx_hostname (hostname),
                INDEX idx_username (username),
                INDEX idx_collected_at (collected_at),
                INDEX idx_motherboard_uuid (motherboard_uuid),
                INDEX idx_user_sid (user_sid)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

        using (var cmd = new MySqlCommand(createSystemHardwareTableSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task MarkAsSyncedAsync(List<int> activityIds)
    {
        if (activityIds.Count == 0) return;

        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var ids = string.Join(",", activityIds);
            var sql = $"UPDATE ActivityLogs SET Synced = 1 WHERE Id IN ({ids})";

            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking activities as synced");
        }
    }

    private async Task DeleteSyncedActivitiesAsync(List<int> activityIds)
    {
        if (activityIds.Count == 0) return;

        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var ids = string.Join(",", activityIds);
            var sql = $"DELETE FROM ActivityLogs WHERE Id IN ({ids})";

            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting synced activities");
        }
    }

    // Browser Data Sync Methods

    private async Task<List<BrowserData>> GetUnsyncedBrowserDataAsync()
    {
        var browserData = new List<BrowserData>();

        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, Url, Title, Browser, VisitTimeUtc, BaseUrl, Username, Domain, CreatedAtUtc
                FROM BrowserDatas 
                WHERE Synced = 0 OR Synced IS NULL
                ORDER BY VisitTimeUtc ASC
                LIMIT @BatchSize";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@BatchSize", _batchSize);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                browserData.Add(new BrowserData
                {
                    Id = reader.GetInt32(0),
                    Url = reader.GetString(1),
                    Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Browser = reader.GetString(3),
                    VisitTimeUtc = DateTime.Parse(reader.GetString(4)),
                    BaseUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Username = reader.GetString(6),
                    Domain = reader.GetString(7),
                    CreatedAtUtc = DateTime.Parse(reader.GetString(8))
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading unsynced browser data from SQLite");
        }

        return browserData;
    }

    private async Task<int> InsertBrowserDataToMysqlAsync(List<BrowserData> browserData)
    {
        int insertedCount = 0;

        try
        {
            using var conn = new MySqlConnection(_mysqlConnectionString);
            await conn.OpenAsync();

            await CreateMysqlTableIfNotExistsAsync(conn);

            using var transaction = await conn.BeginTransactionAsync();

            var sql = @"INSERT INTO browser_datas (
                            url, title, browser, visit_time_utc,
                            base_url, username, domain, user_sid, motherboard_uuid, created_at_utc
                        ) VALUES (
                            @url, @title, @browser, @visit_time_utc,
                            @base_url, @username, @domain, @user_sid, @motherboard_uuid, @created_at_utc
                        )";

            // Get user_sid and motherboard_uuid from environment
            var userSid = SystemIdentityHelper.GetCurrentUserSid();
            var motherboardUuid = SystemIdentityHelper.GetMotherboardUuid();

            foreach (var data in browserData)
            {
                try
                {
                    using var cmd = new MySqlCommand(sql, conn, transaction);

                    cmd.Parameters.AddWithValue("@url", data.Url);
                    cmd.Parameters.AddWithValue("@title", data.Title ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@browser", data.Browser);
                    cmd.Parameters.AddWithValue("@visit_time_utc", data.VisitTimeUtc);
                    cmd.Parameters.AddWithValue("@base_url", data.BaseUrl ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@username", data.Username);
                    cmd.Parameters.AddWithValue("@domain", data.Domain);
                    cmd.Parameters.AddWithValue("@user_sid", userSid);
                    cmd.Parameters.AddWithValue("@motherboard_uuid", motherboardUuid);
                    cmd.Parameters.AddWithValue("@created_at_utc", data.CreatedAtUtc);

                    await cmd.ExecuteNonQueryAsync();
                    insertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error inserting browser data {Id}", data.Id);
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during browser data MySQL insert");
        }

        return insertedCount;
    }

    private async Task MarkBrowserDataAsSyncedAsync(List<int> browserDataIds)
    {
        if (browserDataIds.Count == 0) return;

        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var ids = string.Join(",", browserDataIds);
            var sql = $"UPDATE BrowserDatas SET Synced = 1 WHERE Id IN ({ids})";

            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking browser data as synced");
        }
    }

    private async Task DeleteSyncedBrowserDataAsync(List<int> browserDataIds)
    {
        if (browserDataIds.Count == 0) return;

        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();

            var ids = string.Join(",", browserDataIds);
            var sql = $"DELETE FROM BrowserDatas WHERE Id IN ({ids})";

            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting synced browser data");
        }
    }

    public async Task TriggerSyncAsync()
    {
        _logger.LogInformation("Manual sync triggered");
        await SyncDataAsync();
    }


    private async Task<int> InsertInstalledAppsToMysqlAsync(List<InstalledAppData> apps)
    {
        int insertedCount = 0;
        try
        {
            using var conn = new MySqlConnection(_mysqlConnectionString);
            await conn.OpenAsync();
            await CreateMysqlTableIfNotExistsAsync(conn);

            foreach (var app in apps)
            {
                try
                {
                    var sql = @"INSERT INTO installed_apps (
                        app_name, username, domain, user_sid, motherboard_uuid, machine_name, collected_at
                    ) VALUES (
                        @app_name, @username, @domain, @user_sid, @motherboard_uuid, @machine_name, @collected_at
                    )";

                    using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@app_name", app.AppName);
                    cmd.Parameters.AddWithValue("@username", app.Username);
                    cmd.Parameters.AddWithValue("@domain", app.Domain);
                    cmd.Parameters.AddWithValue("@user_sid", app.UserSid);
                    cmd.Parameters.AddWithValue("@motherboard_uuid", app.MotherboardUuid);
                    cmd.Parameters.AddWithValue("@machine_name", app.MachineName ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@collected_at", app.CollectedAt);

                    await cmd.ExecuteNonQueryAsync();
                    insertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to insert app: {AppName}", app.AppName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during installed apps MySQL insert");
        }
        return insertedCount;
    }

    private async Task<int> InsertSystemHardwareToMysqlAsync(List<SystemHardwareData> hardware)
    {
        int insertedCount = 0;
        try
        {
            using var conn = new MySqlConnection(_mysqlConnectionString);
            await conn.OpenAsync();
            await CreateMysqlTableIfNotExistsAsync(conn);

            foreach (var hw in hardware)
            {
                try
                {
                    var sql = @"INSERT INTO system_hardware (
                        hostname, username, domain, user_sid, os_version, model, serial_number, motherboard_uuid,
                        disk_total_gb, disk_used_gb, disk_free_gb, disk_usage_percent,
                        ram_total_gb, ram_used_gb, ram_free_gb, ram_usage_percent, collected_at
                    ) VALUES (
                        @hostname, @username, @domain, @user_sid, @os_version, @model, @serial_number, @motherboard_uuid,
                        @disk_total_gb, @disk_used_gb, @disk_free_gb, @disk_usage_percent,
                        @ram_total_gb, @ram_used_gb, @ram_free_gb, @ram_usage_percent, @collected_at
                    )";

                    using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@hostname", hw.Hostname);
                    cmd.Parameters.AddWithValue("@username", hw.Username);
                    cmd.Parameters.AddWithValue("@domain", hw.Domain);
                    cmd.Parameters.AddWithValue("@user_sid", hw.UserSid);
                    cmd.Parameters.AddWithValue("@os_version", hw.OsVersion ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@model", hw.Model ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@serial_number", hw.SerialNumber ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@motherboard_uuid", hw.MotherboardUuid ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@disk_total_gb", hw.DiskTotalGB ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@disk_used_gb", hw.DiskUsedGB ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@disk_free_gb", hw.DiskFreeGB ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@disk_usage_percent", hw.DiskUsagePercent ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ram_total_gb", hw.RamTotalGB ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ram_used_gb", hw.RamUsedGB ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ram_free_gb", hw.RamFreeGB ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ram_usage_percent", hw.RamUsagePercent ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@collected_at", hw.CollectedAt);

                    await cmd.ExecuteNonQueryAsync();
                    insertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to insert system hardware");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during system hardware MySQL insert");
        }
        return insertedCount;
    }

    private async Task MarkInstalledAppsAsSyncedAsync(List<int> ids)
    {
        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();
            var sql = $"UPDATE InstalledApps SET Synced = 1 WHERE Id IN ({string.Join(",", ids)})";
            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking installed apps as synced");
        }
    }

    private async Task MarkSystemHardwareAsSyncedAsync(List<int> ids)
    {
        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();
            var sql = $"UPDATE SystemHardware SET Synced = 1 WHERE Id IN ({string.Join(",", ids)})";
            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking system hardware as synced");
        }
    }

    private async Task DeleteSyncedInstalledAppsAsync(List<int> ids)
    {
        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();
            var sql = $"DELETE FROM InstalledApps WHERE Id IN ({string.Join(",", ids)})";
            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting synced installed apps");
        }
    }

    private async Task DeleteSyncedSystemHardwareAsync(List<int> ids)
    {
        try
        {
            using var connection = new SqliteConnection(_sqliteConnectionString);
            await connection.OpenAsync();
            var sql = $"DELETE FROM SystemHardware WHERE Id IN ({string.Join(",", ids)})";
            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting synced system hardware");
        }
    }

    public DataSyncStats GetStats()
    {
        return new DataSyncStats
        {
            Enabled = _enabled,
            MySqlHost = _mysqlHost,
            SyncIntervalMinutes = _syncIntervalMinutes,
            BatchSize = _batchSize,
            DeleteAfterSync = _deleteAfterSync
        };
    }
}

public class DataSyncStats
{
    public bool Enabled { get; set; }
    public string MySqlHost { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; }
    public int BatchSize { get; set; }
    public bool DeleteAfterSync { get; set; }
}

