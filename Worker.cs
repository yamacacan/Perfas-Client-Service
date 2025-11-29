using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using WorkerService1.Services;

namespace WorkerService1
{
    /// <summary>
    /// Ana Worker servisi - tüm veri toplama işlemlerini koordine eder
    /// BackgroundService'den türetilir ve arka planda sürekli çalışır
    /// </summary>
    public class Worker : BackgroundService, IDisposable
    {
        // Temel servis bağımlılıkları
        private readonly ILogger<Worker> _logger;
        private readonly PerformanceAgentConfig _config;
        private readonly TimeSpan _pollInterval;

        // Veri toplama servisleri
        private readonly ConfigurationService _configurationService;
        private readonly SystemInfoService _systemInfoService;
        private readonly UserInfoService _userInfoService;
        private readonly SystemStateService _systemStateService;
        private readonly ApplicationCollectionService _applicationCollectionService;
        private readonly ActivityTrackingService _activityTrackingService;
        private readonly BrowserHistoryService _browserHistoryService;
        private readonly ActivityDatabaseService _databaseService;
        private readonly LoggingService _loggingService;
        private readonly HealthCheckService _healthCheckService;
        private readonly DataSyncService _dataSyncService;
        private readonly IActiveWindowProvider _windowProvider;

        // Dizin yapısı
        private readonly string _baseDir;
        private string _configDir = "";
        private string _dataDir = "";
        private string _systemDir = "";
        private string _activitiesDir = "";
        private string _logsDir = "";
        private string _tempDir = "";

        // Önbellekleme - performans için verileri bellekte tutar
        private List<string>? _installedApps;
        private DateTime _lastAppsRefresh = DateTime.MinValue;

        // Çalışma oturumu ID'si - tüm logları birbirine bağlar
        private readonly Guid _runId = Guid.NewGuid();
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        // System hardware info timer (10 minutes interval)
        private Timer? _systemInfoTimer;
        private const int SYSTEM_INFO_INTERVAL_MINUTES = 10;

        // Kullanıcı bilgileri
        private readonly string _username = Environment.UserName ?? "Unknown";
        private readonly string _domain = Environment.UserDomainName ?? "Unknown";

        public Worker(
            IActiveWindowProvider windowProvider,
            ILogger<Worker> logger,
            ConfigurationService configurationService,
            SystemInfoService systemInfoService,
            UserInfoService userInfoService,
            SystemStateService systemStateService,
            ApplicationCollectionService applicationCollectionService,
            ActivityTrackingService activityTrackingService,
            BrowserHistoryService browserHistoryService,
            ActivityDatabaseService databaseService,
            LoggingService loggingService,
            HealthCheckService healthCheckService,
            DataSyncService dataSyncService,
            PerformanceAgentConfig config)
        {
            _windowProvider = windowProvider;
            _logger = logger;
            _configurationService = configurationService;
            _systemInfoService = systemInfoService;
            _userInfoService = userInfoService;
            _systemStateService = systemStateService;
            _applicationCollectionService = applicationCollectionService;
            _activityTrackingService = activityTrackingService;
            _browserHistoryService = browserHistoryService;
            _databaseService = databaseService;
            _loggingService = loggingService;
            _healthCheckService = healthCheckService;
            _dataSyncService = dataSyncService;
            _config = config;

            _pollInterval = TimeSpan.FromMilliseconds(_config.PollIntervalMs);

            // Initialize directory structure with safe paths
            _baseDir = _configurationService.GetSafeDataDirectory(_config.DataDirectory);
            InitializeDirectoryStructure();
        }

        public new void Dispose()
        {
            _systemInfoTimer?.Dispose();
            _cancellationTokenSource.Dispose();
            base.Dispose();
            GC.SuppressFinalize(this);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Create directory structure
                EnsureDirectoryStructure();

                // Collect initial data
                await CollectInitialDataAsync(stoppingToken);

                // Algoritma BÖLÜM 1: SİSTEM BAŞLATMA VE HAZIRLIK
                // Adım 1.3: Bağımsız Modülleri Başlat - Delil Toplayıcı (BrowserHistoryService)
                // Adım 1.4: Başlangıç Zaman Damgasını Ayarla
                await _browserHistoryService.StartRealTimeMonitoringAsync();

                // Test SQLite database
                TestSQLiteDatabase();

                // Log startup
                await _loggingService.LogInfoAsync("startup",
                    $"Performance Agent started - run_id={_runId}",
                    new { data_directory = _baseDir, activities_dir = _activitiesDir },
                    _runId, stoppingToken);

                // Algoritma BÖLÜM 1: SİSTEM BAŞLATMA VE HAZIRLIK
                // Save system info to SQLite (will sync to MySQL automatically)
                var sysInfo = _systemInfoService.GetSystemInfoData();
                if (sysInfo != null)
                {
                    try
                    {
                        var userSid = SystemIdentityHelper.GetCurrentUserSid();

                        await _databaseService.InsertSystemHardwareAsync(
                            sysInfo,
                            _username,
                            _domain,
                            userSid);

                        _logger.LogInformation("✅ Saved system info to SQLite");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save system info to SQLite");
                    }

                    // Set up timer for periodic system hardware updates (saves to SQLite)
                    _systemInfoTimer = new Timer(
                        async _ => await SaveSystemInfoToSQLiteAsync(stoppingToken),
                        null,
                        TimeSpan.FromMinutes(SYSTEM_INFO_INTERVAL_MINUTES),
                        TimeSpan.FromMinutes(SYSTEM_INFO_INTERVAL_MINUTES));

                    _logger.LogInformation("⏰ System hardware info collection timer started (interval: {Interval} min)", SYSTEM_INFO_INTERVAL_MINUTES);
                }
                // Algoritma BÖLÜM 2: AKTİVİTE GÖZLEMCİSİ'NİN SÜREKLİ GÖREV DÖNGÜSÜ
                // Bu modül, sistem çalıştığı sürece sürekli olarak arka planda çalışır
                await RunMainLoopAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync("execution", "Failed to execute worker", ex, _runId, stoppingToken);
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken ct)
        {
            try
            {
                await _activityTrackingService.LogCurrentActivityAsync(_username, _domain, ct);
                await _loggingService.LogInfoAsync("shutdown", "Performance Agent stopped", null, _runId, ct);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync("shutdown", "Error during shutdown", ex, _runId, ct);
            }
            finally
            {
                await base.StopAsync(ct);
            }
        }

        /// <summary>
        /// Initialize directory structure
        /// </summary>
        private void InitializeDirectoryStructure()
        {
            _configDir = Path.Combine(_baseDir, "config");
            _dataDir = Path.Combine(_baseDir, "data");
            _systemDir = Path.Combine(_dataDir, "system");
            _activitiesDir = Path.Combine(_dataDir, "activities");
            _logsDir = Path.Combine(_baseDir, "logs");
            _tempDir = Path.Combine(_baseDir, "temp");
        }

        /// <summary>
        /// Ensure directory structure exists
        /// </summary>
        private void EnsureDirectoryStructure()
        {
            var directories = new[]
            {
                _configDir, _dataDir, _systemDir, _activitiesDir,
                _logsDir, _tempDir
            };

            foreach (var dir in directories)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create directory {Directory}", dir);
                    throw;
                }
            }
        }

        /// <summary>
        /// Health check endpoint - sistem durumu kontrolü
        /// </summary>
        public async Task<HealthStatus> GetHealthStatusAsync()
        {
            try
            {
                return await _healthCheckService.GetHealthStatusAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get health status");
                return new HealthStatus
                {
                    IsHealthy = false,
                    Timestamp = DateTime.UtcNow,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Test SQLite veritabanı
        /// </summary>
        public void TestSQLiteDatabase()
        {
            try
            {
                string dbPath = Path.Combine(_baseDir, "activities_v2.db");
                _logger.LogInformation("🔍 SQLite Veritabanı Test Ediliyor: {DbPath}", dbPath);

                using var connection = new SqliteConnection($"Data Source={dbPath}");
                connection.Open();

                // Toplam kayıt sayısı
                using var countCommand = new SqliteCommand("SELECT COUNT(*) FROM ActivityLogs", connection);
                var totalCount = countCommand.ExecuteScalar();
                _logger.LogInformation("📊 Toplam Activity Log Kayıtları: {Count}", totalCount);

                // URL'siz kayıt sayısı
                using var nullUrlCommand = new SqliteCommand("SELECT COUNT(*) FROM ActivityLogs WHERE Url IS NULL", connection);
                var nullUrlCount = nullUrlCommand.ExecuteScalar();
                _logger.LogInformation("❌ URL'siz Kayıtlar: {Count}", nullUrlCount);

                // URL'li kayıt sayısı
                using var withUrlCommand = new SqliteCommand("SELECT COUNT(*) FROM ActivityLogs WHERE Url IS NOT NULL", connection);
                var withUrlCount = withUrlCommand.ExecuteScalar();
                _logger.LogInformation("✅ URL'li Kayıtlar: {Count}", withUrlCount);

                // Son 5 kayıt
                _logger.LogInformation("📝 Son 5 Activity Log:");
                using var recentCommand = new SqliteCommand(@"
                    SELECT Id, Evt, ActivityType, ProcessName, Title, StartTimeUtc, EndTimeUtc, DurationMs, Url
                    FROM ActivityLogs 
                    ORDER BY StartTimeUtc DESC 
                    LIMIT 5", connection);

                using var reader = recentCommand.ExecuteReader();
                int rowNum = 1;
                while (reader.Read())
                {
                    _logger.LogInformation("{RowNum}. Evt: {Evt}, Type: {Type}, Process: {Process}, Title: {Title}, Duration: {Duration} ms, URL: {Url}",
                        rowNum, reader["Evt"], reader["ActivityType"], reader["ProcessName"],
                        reader["Title"] == DBNull.Value ? "" : reader["Title"],
                        reader["DurationMs"],
                        reader["Url"] == DBNull.Value ? "NULL" : reader["Url"]);
                    rowNum++;
                }

                // Process istatistikleri
                _logger.LogInformation("📈 Process İstatistikleri:");
                using var processCommand = new SqliteCommand(@"
                    SELECT ProcessName, COUNT(*) as Count, AVG(DurationMs) as AvgDuration
                    FROM ActivityLogs 
                    GROUP BY ProcessName 
                    ORDER BY Count DESC", connection);

                using var processReader = processCommand.ExecuteReader();
                while (processReader.Read())
                {
                    _logger.LogInformation("  - {Process}: {Count} kayıt, Ortalama {AvgDuration:F0} ms",
                        processReader["ProcessName"], processReader["Count"], processReader["AvgDuration"]);
                }

                _logger.LogInformation("✅ SQLite Veritabanı Test Tamamlandı!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ SQLite Test Hatası");
            }
        }

        /// <summary>
        /// Collect initial data at startup
        /// </summary>
        private async Task CollectInitialDataAsync(CancellationToken ct)
        {
            try
            {
                // Initialize SQLite database - Algoritma Adım 1.1
                await _databaseService.InitializeAsync();

                // Collect system information
                await _systemInfoService.CollectSystemInfoAsync(_runId, ct);

                // Collect user information
                await _userInfoService.CollectUserInfoAsync(_runId, ct);

                // Initialize system state monitoring
                await _systemStateService.InitializeAsync(ct);

                // Collect installed applications
                _installedApps = await _applicationCollectionService.CollectInstalledApplicationsAsync(_runId, ct);
                _lastAppsRefresh = DateTime.Now;

                // Save installed apps to SQLite (will sync to MySQL automatically)
                if (_installedApps != null && _installedApps.Count > 0)
                {
                    try
                    {
                        var machineName = Environment.MachineName;
                        var userSid = SystemIdentityHelper.GetCurrentUserSid();
                        var motherboardUuid = SystemIdentityHelper.GetMotherboardUuid();

                        await _databaseService.InsertInstalledAppsBatchAsync(
                            _installedApps,
                            _username,
                            _domain,
                            userSid,
                            motherboardUuid,
                            machineName);

                        _logger.LogInformation("✅ Saved {Count} installed apps to SQLite", _installedApps.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save installed apps to SQLite");
                    }
                }

                // Browser history: Sadece real-time monitoring kullanılıyor
                // CollectBrowserHistoryAsync() kaldırıldı - gereksiz snapshot almıyoruz

                // Start data sync service (internet bağlantısını kontrol edip veri gönderir)
                _logger.LogInformation("🔄 Data sync service initialized");
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync("initial_data", "Failed to collect initial data", ex, _runId, ct);
                throw;
            }
        }

        /// <summary>
        /// Run main monitoring loop with performance optimizations
        /// </summary>
        private async Task RunMainLoopAsync(CancellationToken stoppingToken)
        {
            var lastAppsCheck = DateTime.MinValue;
            var lastSystemStateCheck = DateTime.MinValue;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var (pid, procName, title) = _windowProvider.GetActiveWindowInfo();

                    // Algoritma BÖLÜM 2: AKTİVİTE GÖZLEMCİSİ'NİN SÜREKLİ GÖREV DÖNGÜSÜ
                    // Adım 2.1: Aktif Pencere Değişikliğini Dinle
                    // Adım 2.2: Aktivite Oturumunu Yönet
                    // Adım 2.3: Aktivite Kaydını Veritabanına Yaz
                    await _activityTrackingService.ProcessWindowChangeAsync(pid, procName, title ?? "", _username, _domain, stoppingToken);

                    // Check for periodic data collection (optimized intervals)
                    var now = DateTime.Now;

                    // Check installed apps refresh
                    if (now.Subtract(lastAppsCheck).TotalHours >= _config.AppsRefreshIntervalHours)
                    {
                        try
                        {
                            _installedApps = await _applicationCollectionService.CollectInstalledApplicationsAsync(_runId, stoppingToken);
                            _lastAppsRefresh = now;
                            lastAppsCheck = now;
                        }
                        catch (Exception ex)
                        {
                            await _loggingService.LogErrorAsync("apps_refresh", "Failed to refresh applications", ex, _runId, stoppingToken);
                        }
                    }

                    // Browser history: Real-time monitoring (FileSystemWatcher) kullanılıyor
                    // Periyodik refresh gerekmiyor

                    // Check system state (every 30 seconds)
                    if (now.Subtract(lastSystemStateCheck).TotalSeconds >= 30)
                    {
                        try
                        {
                            await _systemStateService.CheckSystemStateAsync(stoppingToken);
                            lastSystemStateCheck = now;
                        }
                        catch (Exception ex)
                        {
                            await _loggingService.LogErrorAsync("system_state", "Failed to check system state", ex, _runId, stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await _loggingService.LogErrorAsync("main_loop", "Error in main loop", ex, _runId, stoppingToken);
                }

                // Adaptive polling
                var adaptiveInterval = GetAdaptivePollingInterval();
                await Task.Delay(adaptiveInterval, stoppingToken);
            }
        }


        /// <summary>
        /// Get adaptive polling interval based on system activity
        /// </summary>
        private TimeSpan GetAdaptivePollingInterval()
        {
            var baseInterval = _pollInterval;

            if (!_config.EnableDebugLogging)
            {
                baseInterval = TimeSpan.FromMilliseconds(Math.Max(baseInterval.TotalMilliseconds * 1.5, 2000));
            }

            return baseInterval;
        }


        private async Task SaveSystemInfoToSQLiteAsync(CancellationToken _)
        {
            try
            {
                var sysInfo = _systemInfoService.GetSystemInfoData();
                if (sysInfo != null)
                {
                    var userSid = SystemIdentityHelper.GetCurrentUserSid();

                    await _databaseService.InsertSystemHardwareAsync(
                        sysInfo,
                        _username,
                        _domain,
                        userSid);

                    _logger.LogInformation("✅ Periodic system hardware info saved to SQLite");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save periodic system info to SQLite");
            }
        }
    }

    /// <summary>
    /// Active window provider interface for testability
    /// </summary>
    public interface IActiveWindowProvider
    {
        (int pid, string processName, string title) GetActiveWindowInfo();
    }

    /// <summary>
    /// Windows API implementation of active window provider
    /// </summary>
    public class WinApiActiveWindowProvider : IActiveWindowProvider
    {
        public (int pid, string processName, string title) GetActiveWindowInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
                return (-1, "", "");

            _ = GetWindowThreadProcessId(hWnd, out uint pidU);
            int pid = unchecked((int)pidU);

            string title = GetWindowTextRaw(hWnd);
            string processName = "";

            if (pid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    processName = proc.ProcessName;
                }
                catch
                {
                    // Process might have exited
                }
            }

            return (pid, processName ?? "", title ?? "");
        }

        private static string GetWindowTextRaw(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0) return "";
            var sb = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
    }
}