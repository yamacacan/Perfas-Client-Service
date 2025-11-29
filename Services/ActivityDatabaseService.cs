using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Activity veritabanı servisi - SQLite tabanlı
    /// Pencere aktiviteleri ve browser aktivitelerini ayrı kayıtlar olarak saklar
    /// </summary>
    public class ActivityDatabaseService
    {
        private readonly ILogger<ActivityDatabaseService> _logger;
        private readonly string _connectionString;
        private readonly string _databasePath;

        public ActivityDatabaseService(ILogger<ActivityDatabaseService> logger, string dataDirectory)
        {
            _logger = logger;
            _databasePath = System.IO.Path.Combine(dataDirectory, "activities_v2.db");
            // Optimizasyon: Connection pooling ve cache
            _connectionString = $"Data Source={_databasePath};Cache=Shared;Mode=ReadWriteCreate;Pooling=True";
        }

        /// <summary>
        /// Veritabanını başlat ve tabloları oluştur
        /// Schema migration desteği ile - eski veritabanlarını otomatik günceller
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // Optimizasyon: WAL mode aktif et (daha hızlı yazma)
                using var pragmaCommand = new SqliteCommand("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", connection);
                await pragmaCommand.ExecuteNonQueryAsync();

                // Schema version kontrol et
                var currentVersion = await GetSchemaVersionAsync(connection);
                _logger.LogInformation("Current database schema version: {Version}", currentVersion);

                if (currentVersion == 0)
                {
                    // Yeni database - Tabloyu sıfırdan oluştur (v2)
                    await CreateTablesAsync(connection);
                    await SetSchemaVersionAsync(connection, 2);
                    _logger.LogInformation("✅ New database created with schema v2");
                }
                else if (currentVersion < 1)
                {
                    // Eski schema - Migration yap v1'e
                    await MigrateToVersion1Async(connection);
                    await SetSchemaVersionAsync(connection, 1);
                    _logger.LogInformation("✅ Database migrated to schema v1");

                    // v1'den v2'ye migrate et
                    await MigrateToVersion2Async(connection);
                    await SetSchemaVersionAsync(connection, 2);
                    _logger.LogInformation("✅ Database migrated to schema v2");
                }
                else if (currentVersion == 1)
                {
                    // v1'den v2'ye migrate et
                    await MigrateToVersion2Async(connection);
                    await SetSchemaVersionAsync(connection, 2);
                    _logger.LogInformation("✅ Database migrated to schema v2");
                }
                else if (currentVersion < 5)
                {
                    // v4'ten v5'e migrate et - InstalledApps ve SystemHardware tabloları ekle
                    await MigrateToVersion5Async(connection);
                    await SetSchemaVersionAsync(connection, 5);
                    _logger.LogInformation("✅ Database migrated to schema v5 (added InstalledApps & SystemHardware tables)");
                }
                else
                {
                    _logger.LogInformation("✅ Database schema is up to date (v{Version})", currentVersion);
                }

                // Failsafe: Ensure BrowserDatas table exists (in case migration had issues)
                await EnsureBrowserDataTableExistsAsync(connection);

                _logger.LogInformation("Activity database initialized: {DatabasePath}", _databasePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize activity database");
                throw;
            }
        }

        /// <summary>
        /// Schema version'ı kontrol et
        /// </summary>
        private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection)
        {
            try
            {
                // User version PRAGMA kullanarak schema version'ı sakla
                using var command = new SqliteCommand("PRAGMA user_version;", connection);
                var result = await command.ExecuteScalarAsync();
                return result != null ? Convert.ToInt32(result) : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Schema version'ı güncelle
        /// </summary>
        private static async Task SetSchemaVersionAsync(SqliteConnection connection, int version)
        {
            using var command = new SqliteCommand($"PRAGMA user_version = {version};", connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Failsafe: BrowserDatas tablosunun var olduğundan emin ol
        /// Migration sırasında sorun olursa bu tüm eksikleri düzeltir
        /// </summary>
        private async Task EnsureBrowserDataTableExistsAsync(SqliteConnection connection)
        {
            var createBrowserDataSql = @"
                CREATE TABLE IF NOT EXISTS BrowserDatas (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    Title TEXT NULL,
                    Browser TEXT NOT NULL,
                    VisitTimeUtc TEXT NOT NULL,
                    BaseUrl TEXT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Browser ON BrowserDatas(Browser);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_VisitTimeUtc ON BrowserDatas(VisitTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_BaseUrl ON BrowserDatas(BaseUrl) WHERE BaseUrl IS NOT NULL;
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Synced ON BrowserDatas(Synced);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Username ON BrowserDatas(Username);
            ";

            using var createCommand = new SqliteCommand(createBrowserDataSql, connection);
            await createCommand.ExecuteNonQueryAsync();
            _logger.LogDebug("Ensured BrowserDatas table exists");
        }

        /// <summary>
        /// v5 Migration: InstalledApps ve SystemHardware tabloları ekle
        /// </summary>
        private async Task MigrateToVersion5Async(SqliteConnection connection)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS InstalledApps (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AppName TEXT NOT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    UserSid TEXT NOT NULL,
                    MotherboardUuid TEXT NOT NULL,
                    MachineName TEXT,
                    CollectedAt TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_InstalledApps_AppName ON InstalledApps(AppName);
                CREATE INDEX IF NOT EXISTS IX_InstalledApps_Username ON InstalledApps(Username);
                CREATE INDEX IF NOT EXISTS IX_InstalledApps_Synced ON InstalledApps(Synced);
                CREATE INDEX IF NOT EXISTS IX_InstalledApps_CollectedAt ON InstalledApps(CollectedAt);

                CREATE TABLE IF NOT EXISTS SystemHardware (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Hostname TEXT NOT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    UserSid TEXT NOT NULL,
                    OsVersion TEXT,
                    Model TEXT,
                    SerialNumber TEXT,
                    MotherboardUuid TEXT,
                    DiskTotalGB REAL,
                    DiskUsedGB REAL,
                    DiskFreeGB REAL,
                    DiskUsagePercent REAL,
                    RamTotalGB REAL,
                    RamUsedGB REAL,
                    RamFreeGB REAL,
                    RamUsagePercent REAL,
                    CollectedAt TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_SystemHardware_Hostname ON SystemHardware(Hostname);
                CREATE INDEX IF NOT EXISTS IX_SystemHardware_Username ON SystemHardware(Username);
                CREATE INDEX IF NOT EXISTS IX_SystemHardware_Synced ON SystemHardware(Synced);
                CREATE INDEX IF NOT EXISTS IX_SystemHardware_CollectedAt ON SystemHardware(CollectedAt);";

            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            _logger.LogInformation("✅ v5 Migration: Created InstalledApps and SystemHardware tables");
        }

        /// <summary>
        /// Yeni database için tabloları oluştur (v2 schema)
        /// </summary>
        private static async Task CreateTablesAsync(SqliteConnection connection)
        {
            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS ActivityLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Evt TEXT NOT NULL DEFAULT 'activity',
                    ActivityType TEXT NOT NULL DEFAULT 'window',
                    ProcessName TEXT NOT NULL,
                    Title TEXT,
                    StartTimeUtc TEXT NOT NULL,
                    EndTimeUtc TEXT NOT NULL,
                    DurationMs INTEGER NOT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    UserSid TEXT NOT NULL,
                    MotherboardUuid TEXT NOT NULL,
                    Url TEXT NULL,
                    Browser TEXT NULL,
                    BaseUrl TEXT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Evt ON ActivityLogs(Evt);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_ActivityType ON ActivityLogs(ActivityType);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_ProcessName ON ActivityLogs(ProcessName);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_StartTimeUtc ON ActivityLogs(StartTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_EndTimeUtc ON ActivityLogs(EndTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Browser ON ActivityLogs(Browser) WHERE Browser IS NOT NULL;
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_BaseUrl ON ActivityLogs(BaseUrl) WHERE BaseUrl IS NOT NULL;
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Synced ON ActivityLogs(Synced);

                CREATE TABLE IF NOT EXISTS BrowserDatas (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    Title TEXT NULL,
                    Browser TEXT NOT NULL,
                    VisitTimeUtc TEXT NOT NULL,
                    BaseUrl TEXT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Browser ON BrowserDatas(Browser);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_VisitTimeUtc ON BrowserDatas(VisitTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_BaseUrl ON BrowserDatas(BaseUrl) WHERE BaseUrl IS NOT NULL;
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Synced ON BrowserDatas(Synced);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Username ON BrowserDatas(Username);

                CREATE TABLE IF NOT EXISTS InstalledApps (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AppName TEXT NOT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    UserSid TEXT NOT NULL,
                    MotherboardUuid TEXT NOT NULL,
                    MachineName TEXT,
                    CollectedAt TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_InstalledApps_AppName ON InstalledApps(AppName);
                CREATE INDEX IF NOT EXISTS IX_InstalledApps_Username ON InstalledApps(Username);
                CREATE INDEX IF NOT EXISTS IX_InstalledApps_Synced ON InstalledApps(Synced);
                CREATE INDEX IF NOT EXISTS IX_InstalledApps_CollectedAt ON InstalledApps(CollectedAt);

                CREATE TABLE IF NOT EXISTS SystemHardware (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Hostname TEXT NOT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    UserSid TEXT NOT NULL,
                    OsVersion TEXT,
                    Model TEXT,
                    SerialNumber TEXT,
                    MotherboardUuid TEXT,
                    DiskTotalGB REAL,
                    DiskUsedGB REAL,
                    DiskFreeGB REAL,
                    DiskUsagePercent REAL,
                    RamTotalGB REAL,
                    RamUsedGB REAL,
                    RamFreeGB REAL,
                    RamUsagePercent REAL,
                    CollectedAt TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_SystemHardware_Hostname ON SystemHardware(Hostname);
                CREATE INDEX IF NOT EXISTS IX_SystemHardware_Username ON SystemHardware(Username);
                CREATE INDEX IF NOT EXISTS IX_SystemHardware_Synced ON SystemHardware(Synced);
                CREATE INDEX IF NOT EXISTS IX_SystemHardware_CollectedAt ON SystemHardware(CollectedAt);
            ";

            using var command = new SqliteCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Eski database'i v1 schema'ya migrate et
        /// </summary>
        private async Task MigrateToVersion1Async(SqliteConnection connection)
        {
            // Tablo var mı kontrol et
            var tableExistsSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='ActivityLogs';";
            using var checkCommand = new SqliteCommand(tableExistsSql, connection);
            var tableExists = await checkCommand.ExecuteScalarAsync() != null;

            if (!tableExists)
            {
                // Tablo yoksa - yeni oluştur
                await CreateTablesAsync(connection);
                return;
            }

            // Kolonları kontrol et ve eksikleri ekle
            var columns = new List<string>();
            var columnCheckSql = "PRAGMA table_info(ActivityLogs);";
            using var columnCommand = new SqliteCommand(columnCheckSql, connection);
            using var reader = await columnCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1)); // Column name
            }

            // Eksik kolonları ekle
            var alterCommands = new List<string>();

            if (!columns.Contains("Evt"))
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN Evt TEXT NOT NULL DEFAULT 'activity';");

            if (!columns.Contains("ActivityType"))
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN ActivityType TEXT NOT NULL DEFAULT 'window';");

            if (!columns.Contains("Title") && columns.Contains("WindowTitle"))
            {
                // WindowTitle'ı Title'a rename et (SQLite doesn't support RENAME COLUMN before 3.25.0)
                // Workaround: Yeni kolon ekle ve veriyi kopyala
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN Title TEXT;");
                alterCommands.Add("UPDATE ActivityLogs SET Title = WindowTitle WHERE Title IS NULL;");
            }
            else if (!columns.Contains("Title"))
            {
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN Title TEXT;");
            }

            if (!columns.Contains("Url"))
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN Url TEXT NULL;");

            if (!columns.Contains("Browser"))
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN Browser TEXT NULL;");

            if (!columns.Contains("Synced"))
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN Synced INTEGER NOT NULL DEFAULT 0;");

            // ALTER komutlarını çalıştır
            foreach (var alterSql in alterCommands)
            {
                using var alterCommand = new SqliteCommand(alterSql, connection);
                await alterCommand.ExecuteNonQueryAsync();
                _logger.LogInformation("Migration: {Command}", alterSql);
            }

            // Index'leri oluştur (IF NOT EXISTS kullan)
            var createIndexSql = @"
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Evt ON ActivityLogs(Evt);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_ActivityType ON ActivityLogs(ActivityType);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_ProcessName ON ActivityLogs(ProcessName);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_StartTimeUtc ON ActivityLogs(StartTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_EndTimeUtc ON ActivityLogs(EndTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Browser ON ActivityLogs(Browser) WHERE Browser IS NOT NULL;
                CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Synced ON ActivityLogs(Synced);
            ";

            using var indexCommand = new SqliteCommand(createIndexSql, connection);
            await indexCommand.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// v1 schema'yı v2'ye migrate et - BrowserDatas tablosu ve BaseUrl kolonları ekle
        /// </summary>
        private async Task MigrateToVersion2Async(SqliteConnection connection)
        {
            _logger.LogInformation("Starting migration to schema v2...");

            // ActivityLogs tablosuna BaseUrl kolonu ekle
            var columns = new List<string>();
            var columnCheckSql = "PRAGMA table_info(ActivityLogs);";
            using var columnCommand = new SqliteCommand(columnCheckSql, connection);
            using var reader = await columnCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1)); // Column name
            }

            var alterCommands = new List<string>();

            if (!columns.Contains("BaseUrl"))
            {
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN BaseUrl TEXT NULL;");
                _logger.LogInformation("Adding BaseUrl column to ActivityLogs");
            }

            if (!columns.Contains("UserSid"))
            {
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN UserSid TEXT NOT NULL DEFAULT '';");
                _logger.LogInformation("Adding UserSid column to ActivityLogs");
            }

            if (!columns.Contains("MotherboardUuid"))
            {
                alterCommands.Add("ALTER TABLE ActivityLogs ADD COLUMN MotherboardUuid TEXT NOT NULL DEFAULT '';");
                _logger.LogInformation("Adding MotherboardUuid column to ActivityLogs");
            }

            // ALTER komutlarını çalıştır
            foreach (var alterSql in alterCommands)
            {
                using var alterCommand = new SqliteCommand(alterSql, connection);
                await alterCommand.ExecuteNonQueryAsync();
                _logger.LogInformation("Migration v2: {Command}", alterSql);
            }

            // BrowserDatas tablosu oluştur
            var createBrowserDataSql = @"
                CREATE TABLE IF NOT EXISTS BrowserDatas (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    Title TEXT NULL,
                    Browser TEXT NOT NULL,
                    VisitTimeUtc TEXT NOT NULL,
                    BaseUrl TEXT NULL,
                    Username TEXT NOT NULL,
                    Domain TEXT NOT NULL,
                    Synced INTEGER NOT NULL DEFAULT 0,
                    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Browser ON BrowserDatas(Browser);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_VisitTimeUtc ON BrowserDatas(VisitTimeUtc);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_BaseUrl ON BrowserDatas(BaseUrl) WHERE BaseUrl IS NOT NULL;
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Synced ON BrowserDatas(Synced);
                CREATE INDEX IF NOT EXISTS IX_BrowserDatas_Username ON BrowserDatas(Username);
            ";

            using var createCommand = new SqliteCommand(createBrowserDataSql, connection);
            await createCommand.ExecuteNonQueryAsync();
            _logger.LogInformation("Created BrowserDatas table with indexes");

            // ActivityLogs BaseUrl index ekle
            var createBaseUrlIndexSql = "CREATE INDEX IF NOT EXISTS IX_ActivityLogs_BaseUrl ON ActivityLogs(BaseUrl) WHERE BaseUrl IS NOT NULL;";
            using var indexCommand2 = new SqliteCommand(createBaseUrlIndexSql, connection);
            await indexCommand2.ExecuteNonQueryAsync();

            _logger.LogInformation("✅ Migration to v2 completed successfully");
        }

        /// <summary>
        /// Batch insert - Birden fazla activity'yi tek seferde ekle (performans optimizasyonu)
        /// </summary>
        public async Task InsertActivitiesBatchAsync(List<ActivityLog> activities)
        {
            if (activities.Count == 0) return;

            const int maxRetries = 3;
            var retryDelay = TimeSpan.FromMilliseconds(100);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();

                    using var transaction = connection.BeginTransaction();

                    var sql = @"
                        INSERT INTO ActivityLogs 
                        (Evt, ActivityType, ProcessName, Title, StartTimeUtc, EndTimeUtc, DurationMs, Username, Domain, Url, Browser, BaseUrl)
                        VALUES 
                        (@Evt, @ActivityType, @ProcessName, @Title, @StartTimeUtc, @EndTimeUtc, @DurationMs, @Username, @Domain, @Url, @Browser, @BaseUrl)
                    ";

                    using var command = new SqliteCommand(sql, connection, transaction);

                    foreach (var activity in activities)
                    {
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@Evt", activity.Evt ?? "activity");
                        command.Parameters.AddWithValue("@ActivityType", activity.ActivityType);
                        command.Parameters.AddWithValue("@ProcessName", activity.ProcessName);
                        command.Parameters.AddWithValue("@Title", activity.Title ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@StartTimeUtc", activity.StartTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                        command.Parameters.AddWithValue("@EndTimeUtc", activity.EndTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                        command.Parameters.AddWithValue("@DurationMs", activity.DurationMs);
                        command.Parameters.AddWithValue("@Username", activity.Username);
                        command.Parameters.AddWithValue("@Domain", activity.Domain);
                        command.Parameters.AddWithValue("@Url", activity.Url ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Browser", activity.Browser ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@BaseUrl", activity.BaseUrl ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Batch insert attempt {Attempt} failed, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
            }
        }

        /// <summary>
        /// Activity log ekle - Window veya Browser activity
        /// </summary>
        public async Task InsertActivityAsync(ActivityLog activity)
        {
            const int maxRetries = 3;
            var retryDelay = TimeSpan.FromMilliseconds(100);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO ActivityLogs 
                        (Evt, ActivityType, ProcessName, Title, StartTimeUtc, EndTimeUtc, DurationMs, Username, Domain, UserSid, MotherboardUuid, Url, Browser, BaseUrl)
                        VALUES 
                        (@Evt, @ActivityType, @ProcessName, @Title, @StartTimeUtc, @EndTimeUtc, @DurationMs, @Username, @Domain, @UserSid, @MotherboardUuid, @Url, @Browser, @BaseUrl)
                    ";

                    using var command = new SqliteCommand(sql, connection);
                    command.Parameters.AddWithValue("@Evt", activity.Evt ?? "activity");
                    command.Parameters.AddWithValue("@ActivityType", activity.ActivityType);
                    command.Parameters.AddWithValue("@ProcessName", activity.ProcessName);
                    command.Parameters.AddWithValue("@Title", activity.Title ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@StartTimeUtc", activity.StartTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                    command.Parameters.AddWithValue("@EndTimeUtc", activity.EndTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                    command.Parameters.AddWithValue("@DurationMs", activity.DurationMs);
                    command.Parameters.AddWithValue("@Username", activity.Username);
                    command.Parameters.AddWithValue("@Domain", activity.Domain);
                    command.Parameters.AddWithValue("@UserSid", activity.UserSid);
                    command.Parameters.AddWithValue("@MotherboardUuid", activity.MotherboardUuid);
                    command.Parameters.AddWithValue("@Url", activity.Url ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@Browser", activity.Browser ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@BaseUrl", activity.BaseUrl ?? (object)DBNull.Value);

                    await command.ExecuteNonQueryAsync();
                    return; // Success
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && attempt < maxRetries) // SQLITE_CONSTRAINT
                {
                    _logger.LogWarning(ex, "Constraint violation on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries) // SQLITE_BUSY
                {
                    _logger.LogWarning(ex, "Database busy on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 6 && attempt < maxRetries) // SQLITE_LOCKED
                {
                    _logger.LogWarning(ex, "Database locked on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Database error on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to insert activity log after {Attempts} attempts", maxRetries);
                    throw;
                }
            }
        }

        /// <summary>
        /// Browser data ekle - Tarayıcı geçmişi verisi
        /// </summary>
        public async Task InsertBrowserDataAsync(BrowserData data)
        {
            const int maxRetries = 3;
            var retryDelay = TimeSpan.FromMilliseconds(100);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO BrowserDatas 
                        (Url, Title, Browser, VisitTimeUtc, BaseUrl, Username, Domain)
                        VALUES 
                        (@Url, @Title, @Browser, @VisitTimeUtc, @BaseUrl, @Username, @Domain)
                    ";

                    using var command = new SqliteCommand(sql, connection);
                    command.Parameters.AddWithValue("@Url", data.Url);
                    command.Parameters.AddWithValue("@Title", data.Title ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@Browser", data.Browser);
                    command.Parameters.AddWithValue("@VisitTimeUtc", data.VisitTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                    command.Parameters.AddWithValue("@BaseUrl", data.BaseUrl ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@Username", data.Username);
                    command.Parameters.AddWithValue("@Domain", data.Domain);

                    await command.ExecuteNonQueryAsync();
                    return; // Success
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries) // SQLITE_BUSY
                {
                    _logger.LogWarning(ex, "Database busy on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 6 && attempt < maxRetries) // SQLITE_LOCKED
                {
                    _logger.LogWarning(ex, "Database locked on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Database error on attempt {Attempt}, retrying...", attempt);
                    await Task.Delay(retryDelay * attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to insert browser data after {Attempts} attempts", maxRetries);
                    throw;
                }
            }
        }

        /// <summary>
        /// Senkronize edilmemiş browser verilerini getir
        /// </summary>
        public async Task<List<BrowserData>> GetUnsyncedBrowserDataAsync(int limit = 100)
        {
            var browserData = new List<BrowserData>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT Id, Url, Title, Browser, VisitTimeUtc, BaseUrl, Username, Domain, CreatedAtUtc
                    FROM BrowserDatas 
                    WHERE Synced = 0 OR Synced IS NULL
                    ORDER BY VisitTimeUtc ASC
                    LIMIT @Limit";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@Limit", limit);

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

        /// <summary>
        /// Installed apps batch ekle - Uygulama başlangıcında toplanan uygulamalar
        /// </summary>
        public async Task InsertInstalledAppsBatchAsync(List<string> apps, string username, string domain, string userSid, string motherboardUuid, string machineName)
        {
            if (apps.Count == 0) return;

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();

                var sql = @"
                    INSERT INTO InstalledApps 
                    (AppName, Username, Domain, UserSid, MotherboardUuid, MachineName, CollectedAt)
                    VALUES 
                    (@AppName, @Username, @Domain, @UserSid, @MotherboardUuid, @MachineName, @CollectedAt)";

                var collectedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

                foreach (var app in apps)
                {
                    using var command = new SqliteCommand(sql, connection, transaction);
                    command.Parameters.AddWithValue("@AppName", app);
                    command.Parameters.AddWithValue("@Username", username);
                    command.Parameters.AddWithValue("@Domain", domain);
                    command.Parameters.AddWithValue("@UserSid", userSid);
                    command.Parameters.AddWithValue("@MotherboardUuid", motherboardUuid);
                    command.Parameters.AddWithValue("@MachineName", machineName ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@CollectedAt", collectedAt);

                    await command.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                _logger.LogInformation("Inserted {Count} installed apps to SQLite", apps.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert installed apps batch");
                throw;
            }
        }

        /// <summary>
        /// System hardware ekle - Periyodik olarak toplanan donanım bilgisi
        /// </summary>
        public async Task InsertSystemHardwareAsync(SystemInfoData sysInfo, string username, string domain, string userSid)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    INSERT INTO SystemHardware 
                    (Hostname, Username, Domain, UserSid, OsVersion, Model, SerialNumber, MotherboardUuid,
                     DiskTotalGB, DiskUsedGB, DiskFreeGB, DiskUsagePercent,
                     RamTotalGB, RamUsedGB, RamFreeGB, RamUsagePercent, CollectedAt)
                    VALUES 
                    (@Hostname, @Username, @Domain, @UserSid, @OsVersion, @Model, @SerialNumber, @MotherboardUuid,
                     @DiskTotalGB, @DiskUsedGB, @DiskFreeGB, @DiskUsagePercent,
                     @RamTotalGB, @RamUsedGB, @RamFreeGB, @RamUsagePercent, @CollectedAt)";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@Hostname", sysInfo.HostName);
                command.Parameters.AddWithValue("@Username", username);
                command.Parameters.AddWithValue("@Domain", domain);
                command.Parameters.AddWithValue("@UserSid", userSid);
                command.Parameters.AddWithValue("@OsVersion", sysInfo.OsVersion ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Model", sysInfo.Model ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@SerialNumber", sysInfo.SerialNumber ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@MotherboardUuid", sysInfo.MotherboardUuid ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@DiskTotalGB", sysInfo.DiskTotalGB);
                command.Parameters.AddWithValue("@DiskUsedGB", sysInfo.DiskUsedGB);
                command.Parameters.AddWithValue("@DiskFreeGB", sysInfo.DiskFreeGB);
                command.Parameters.AddWithValue("@DiskUsagePercent", sysInfo.DiskUsagePercent);
                command.Parameters.AddWithValue("@RamTotalGB", sysInfo.RamTotalGB);
                command.Parameters.AddWithValue("@RamUsedGB", sysInfo.RamUsedGB);
                command.Parameters.AddWithValue("@RamFreeGB", sysInfo.RamFreeGB);
                command.Parameters.AddWithValue("@RamUsagePercent", sysInfo.RamUsagePercent);
                command.Parameters.AddWithValue("@CollectedAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));

                await command.ExecuteNonQueryAsync();
                _logger.LogDebug("Inserted system hardware info to SQLite");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert system hardware");
                throw;
            }
        }

        /// <summary>
        /// Senkronize edilmemiş installed apps'i getir
        /// </summary>
        public async Task<List<InstalledAppData>> GetUnsyncedInstalledAppsAsync(int limit = 100)
        {
            var apps = new List<InstalledAppData>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT Id, AppName, Username, Domain, UserSid, MotherboardUuid, MachineName, CollectedAt, CreatedAtUtc
                    FROM InstalledApps 
                    WHERE Synced = 0 OR Synced IS NULL
                    ORDER BY CollectedAt ASC
                    LIMIT @Limit";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@Limit", limit);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    apps.Add(new InstalledAppData
                    {
                        Id = reader.GetInt32(0),
                        AppName = reader.GetString(1),
                        Username = reader.GetString(2),
                        Domain = reader.GetString(3),
                        UserSid = reader.GetString(4),
                        MotherboardUuid = reader.GetString(5),
                        MachineName = reader.IsDBNull(6) ? null : reader.GetString(6),
                        CollectedAt = DateTime.Parse(reader.GetString(7)),
                        CreatedAtUtc = DateTime.Parse(reader.GetString(8))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading unsynced installed apps from SQLite");
            }

            return apps;
        }

        /// <summary>
        /// Senkronize edilmemiş system hardware'i getir
        /// </summary>
        public async Task<List<SystemHardwareData>> GetUnsyncedSystemHardwareAsync(int limit = 100)
        {
            var hardware = new List<SystemHardwareData>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT Id, Hostname, Username, Domain, UserSid, OsVersion, Model, SerialNumber, MotherboardUuid,
                           DiskTotalGB, DiskUsedGB, DiskFreeGB, DiskUsagePercent,
                           RamTotalGB, RamUsedGB, RamFreeGB, RamUsagePercent, CollectedAt, CreatedAtUtc
                    FROM SystemHardware 
                    WHERE Synced = 0 OR Synced IS NULL
                    ORDER BY CollectedAt ASC
                    LIMIT @Limit";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@Limit", limit);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    hardware.Add(new SystemHardwareData
                    {
                        Id = reader.GetInt32(0),
                        Hostname = reader.GetString(1),
                        Username = reader.GetString(2),
                        Domain = reader.GetString(3),
                        UserSid = reader.GetString(4),
                        OsVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Model = reader.IsDBNull(6) ? null : reader.GetString(6),
                        SerialNumber = reader.IsDBNull(7) ? null : reader.GetString(7),
                        MotherboardUuid = reader.IsDBNull(8) ? null : reader.GetString(8),
                        DiskTotalGB = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        DiskUsedGB = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                        DiskFreeGB = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                        DiskUsagePercent = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                        RamTotalGB = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                        RamUsedGB = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                        RamFreeGB = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                        RamUsagePercent = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                        CollectedAt = DateTime.Parse(reader.GetString(17)),
                        CreatedAtUtc = DateTime.Parse(reader.GetString(18))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading unsynced system hardware from SQLite");
            }

            return hardware;
        }

    }

    /// <summary>
    /// Activity log veri modeli
    /// Pencere aktiviteleri ve browser aktivitelerini temsil eder
    /// </summary>
    public class ActivityLog
    {
        /// <summary>
        /// Veritabanı ID (AUTO INCREMENT)
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Event tipi: "activity", "system_state", vb.
        /// JSON'daki "evt" alanına karşılık gelir
        /// </summary>
        public string Evt { get; set; } = "activity";

        /// <summary>
        /// Aktivite tipi: "window" (pencere) veya "browser" (tarayıcı URL)
        /// JSON'daki "activity_type" alanına karşılık gelir
        /// </summary>
        public string ActivityType { get; set; } = "window";

        public string ProcessName { get; set; } = string.Empty;

        /// <summary>
        /// Title: Window aktiviteleri için window title, Browser aktiviteleri için page title
        /// </summary>
        public string? Title { get; set; }

        public DateTime StartTimeUtc { get; set; }
        public DateTime EndTimeUtc { get; set; }
        public long DurationMs { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;

        /// <summary>
        /// User Security Identifier (SID) - Windows kullanıcı tanımlayıcısı
        /// </summary>
        public string UserSid { get; set; } = string.Empty;

        /// <summary>
        /// Motherboard UUID - Donanım tanımlayıcısı
        /// </summary>
        public string MotherboardUuid { get; set; } = string.Empty;

        // Browser activity için ek alanlar
        public string? Url { get; set; }
        public string? Browser { get; set; }

        /// <summary>
        /// Base URL (protocol + domain) - Hem window hem browser aktiviteleri için
        /// Örnek: https://www.google.com
        /// </summary>
        public string? BaseUrl { get; set; }

        // Senkronizasyon için ek alanlar
        /// <summary>
        /// Sunucuya senkronize edilmiş mi? (0 = Hayır, 1 = Evet)
        /// </summary>
        public int Synced { get; set; }

        /// <summary>
        /// Veritabanına eklenme zamanı
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// Browser data veri modeli
    /// Tarayıcı geçmişi verilerini ayrı tabloda saklar
    /// </summary>
    public class BrowserData
    {
        /// <summary>
        /// Veritabanı ID (AUTO INCREMENT)
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Ziyaret edilen URL
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Sayfa başlığı
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// Tarayıcı adı (Chrome, Edge, Firefox, vb.)
        /// </summary>
        public string Browser { get; set; } = string.Empty;

        /// <summary>
        /// Ziyaret zamanı (UTC)
        /// </summary>
        public DateTime VisitTimeUtc { get; set; }

        /// <summary>
        /// Base URL (protocol + domain)
        /// Örnek: https://www.google.com
        /// </summary>
        public string? BaseUrl { get; set; }

        /// <summary>
        /// Kullanıcı adı
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Domain
        /// </summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>
        /// Sunucuya senkronize edilmiş mi? (0 = Hayır, 1 = Evet)
        /// </summary>
        public int Synced { get; set; }

        /// <summary>
        /// Veritabanına eklenme zamanı
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// Installed apps veri modeli
    /// </summary>
    public class InstalledAppData
    {
        public int Id { get; set; }
        public string AppName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string UserSid { get; set; } = string.Empty;
        public string MotherboardUuid { get; set; } = string.Empty;
        public string? MachineName { get; set; }
        public DateTime CollectedAt { get; set; }
        public int Synced { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// System hardware veri modeli
    /// </summary>
    public class SystemHardwareData
    {
        public int Id { get; set; }
        public string Hostname { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string UserSid { get; set; } = string.Empty;
        public string? OsVersion { get; set; }
        public string? Model { get; set; }
        public string? SerialNumber { get; set; }
        public string? MotherboardUuid { get; set; }
        public double? DiskTotalGB { get; set; }
        public double? DiskUsedGB { get; set; }
        public double? DiskFreeGB { get; set; }
        public double? DiskUsagePercent { get; set; }
        public double? RamTotalGB { get; set; }
        public double? RamUsedGB { get; set; }
        public double? RamFreeGB { get; set; }
        public double? RamUsagePercent { get; set; }
        public DateTime CollectedAt { get; set; }
        public int Synced { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
