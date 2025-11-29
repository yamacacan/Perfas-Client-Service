using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Tarayıcı geçmişi toplama servisi - Chromium tabanlı ve Firefox tarayıcıları için
    /// Chrome, Edge ve Firefox tarayıcılarının geçmiş verilerini toplar ve birleştirir
    /// </summary>
    public class BrowserHistoryService
    {
        // Chrome epoch - Chrome'un zaman damgası başlangıcı (1601-01-01)
        private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Servis bağımlılıkları
        private readonly ILogger<BrowserHistoryService> _logger;
        private readonly string _dataDirectory; // Veri dizini

        // Real-time monitoring fields
        private readonly string _activitiesDir; // Activity logların kaydedileceği dizin
        private readonly string _username; // Kullanıcı adı
        private readonly string _domain; // Domain adı
        private readonly BrowserHistoryMonitoringOptions _monitoringOptions; // Monitoring yapılandırması
        private readonly Dictionary<string, BrowserWatcher> _watchers = new(); // Browser watcher'lar
        private readonly ActivityDatabaseService _databaseService; // SQLite veritabanı servisi

        // Optimizasyon: In-memory duplicate cache (100x daha hızlı)
        private readonly ConcurrentDictionary<string, DateTime> _urlCache = new();
        private Timer? _cacheCleanupTimer;

        // Optimizasyon: Cached configuration values
        private readonly int _debounceDelayMs;
        private readonly int _sqliteCommitDelayMs;
        private readonly int _recentUrlsSeconds;

        /// <summary>
        /// Browser watcher helper class
        /// </summary>
        private class BrowserWatcher : IDisposable
        {
            public FileSystemWatcher? Watcher { get; set; }
            public CancellationTokenSource DebounceCts { get; set; } = new();
            public DateTime FirstEventTime { get; set; } = DateTime.MinValue;
            public DateTime LastProcessTime { get; set; } = DateTime.UtcNow;
            public SemaphoreSlim Lock { get; } = new(1, 1);
            public long LastChromiumTimestamp { get; set; }
            public long LastFirefoxTimestamp { get; set; }
            public Timer? BackupTimer { get; set; }
            public DateTime LastBackupCheck { get; set; } = DateTime.UtcNow;
            public int ErrorCount { get; set; } = 0;
            public DateTime LastErrorTime { get; set; } = DateTime.MinValue;

            public void Dispose()
            {
                Watcher?.Dispose();
                DebounceCts?.Dispose();
                BackupTimer?.Dispose();
                Lock?.Dispose();
            }
        }

        /// <summary>
        /// Constructor - servis bağımlılıklarını alır
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="dataDirectory">Veri dizini</param>
        /// <param name="databaseService">SQLite veritabanı servisi</param>
        /// <param name="activitiesDir">Activity log dizini (real-time için)</param>
        /// <param name="username">Kullanıcı adı (real-time için)</param>
        /// <param name="domain">Domain adı (real-time için)</param>
        /// <param name="monitoringOptions">Real-time monitoring seçenekleri</param>
        public BrowserHistoryService(
            ILogger<BrowserHistoryService> logger,
            string dataDirectory,
            ActivityDatabaseService databaseService,
            string? activitiesDir = null,
            string? username = null,
            string? domain = null,
            BrowserHistoryMonitoringOptions? monitoringOptions = null)
        {
            _logger = logger;
            _dataDirectory = dataDirectory;
            _databaseService = databaseService;

            // Real-time monitoring setup
            _activitiesDir = activitiesDir ?? Path.Combine(dataDirectory, "activities");
            _username = username ?? Environment.UserName ?? "Unknown";
            _domain = domain ?? Environment.UserDomainName ?? "Unknown";
            _monitoringOptions = monitoringOptions ?? new BrowserHistoryMonitoringOptions();

            // Optimizasyon: Cache configuration values
            _debounceDelayMs = _monitoringOptions.DebounceDelayMs;
            _sqliteCommitDelayMs = _monitoringOptions.SqliteCommitDelayMs;
            _recentUrlsSeconds = _monitoringOptions.RecentUrlsSeconds;

            // Optimizasyon: Cache cleanup timer (her 5 dakikada bir)
            _cacheCleanupTimer = new Timer(CleanupUrlCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// URL cache temizleme - 10 dakikadan eski kayıtları sil
        /// </summary>
        private void CleanupUrlCache(object? state)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddMinutes(-10);
                var keysToRemove = _urlCache.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();

                foreach (var key in keysToRemove)
                {
                    _urlCache.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup URL cache");
            }
        }

        /// <summary>
        /// Çıktı dizini - salt okunur özellik
        /// </summary>
        public string OutputDirectory => _dataDirectory;

        /// <summary>
        /// Konfigüre edilmiş tarayıcı etkinleştirme ve sınırlara göre tarayıcı geçmişi girişlerini topla
        /// </summary>
        /// <param name="ct">İptal token'ı</param>
        /// <returns>Toplanan tarayıcı geçmişi girişleri</returns>
        public async Task<List<BrowserHistoryEntry>> CollectBrowserHistoryAsync(CancellationToken ct)
        {
            // ⚠️ DEPRECATED: Bu metod artık kullanılmıyor (Real-time monitoring aktif)
            await Task.CompletedTask; // async uyumluluğu için
            _logger.LogWarning("CollectBrowserHistoryAsync is deprecated and not used");
            return new List<BrowserHistoryEntry>();

            /* DEPRECATED CODE - Batch collection removed
            var aggregated = new List<BrowserHistoryEntry>();

            try
            {
                // Hiçbir tarayıcı etkin değilse atla
                if (true) // _options.EnableAnyBrowser
                {
                    _logger.LogWarning("All browsers disabled by configuration; skipping history collection.");
                    return aggregated;
                }

                if (_options.EnableChrome)
                {
                    var chromeHistory = await GetChromeHistoryAsync(ct);
                    aggregated.AddRange(chromeHistory);
                    _logger.LogDebug("Collected {Count} Chrome history entries", chromeHistory.Count);
                }
                else
                {
                    _logger.LogDebug("Chrome history collection disabled by configuration.");
                }

                ct.ThrowIfCancellationRequested();

                if (_options.EnableEdge)
                {
                    var edgeHistory = await GetEdgeHistoryAsync(ct);
                    aggregated.AddRange(edgeHistory);
                    _logger.LogDebug("Collected {Count} Edge history entries", edgeHistory.Count);
                }
                else
                {
                    _logger.LogDebug("Edge history collection disabled by configuration.");
                }

                ct.ThrowIfCancellationRequested();

                if (_options.EnableFirefox)
                {
                    var firefoxHistory = await GetFirefoxHistoryAsync(ct);
                    aggregated.AddRange(firefoxHistory);
                    _logger.LogDebug("Collected {Count} Firefox history entries", firefoxHistory.Count);
                }
                else
                {
                    _logger.LogDebug("Firefox history collection disabled by configuration.");
                }

                var deduped = aggregated
                    .GroupBy(h => (Browser: h.Browser, Url: h.Url, h.LastVisitTime))
                    .Select(g => g.First())
                    .OrderByDescending(h => h.LastVisitTime)
                    .Take(_maxEntries)
                    .ToList();

                var perBrowser = deduped.GroupBy(h => h.Browser)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            */
        }

        /// <summary>
        /// Persist collected browser history to disk with summary metadata.
        /// </summary>
        public async Task SaveBrowserHistoryAsync(IEnumerable<BrowserHistoryEntry> history, Guid runId, CancellationToken ct)
        {
            // ⚠️ DEPRECATED: Bu metod artık kullanılmıyor (Real-time monitoring aktif)
            await Task.CompletedTask;
            _logger.LogWarning("SaveBrowserHistoryAsync is deprecated and not used");
            return;
            /*
            try
            {
                var historyList = history
                    .OrderByDescending(h => h.LastVisitTime)
                    .Take(_maxEntries)
                    .ToList();

                var perBrowser = historyList.GroupBy(h => h.Browser)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                var payload = new
                {
                    collected_at = DateTimeOffset.UtcNow,
                    run_id = runId,
                    total = historyList.Count,
                    per_browser = perBrowser,
                    max_entries = _maxEntries,
                    entries = historyList
                };

                var historyDir = Path.Combine(_dataDirectory, "data", "browser");
                Directory.CreateDirectory(historyDir);

                var fileName = $"browser-history-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
                var historyPath = Path.Combine(historyDir, fileName);

                ct.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(payload, _serializerOptions);
                await File.WriteAllTextAsync(historyPath, json, ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Cancelled while saving browser history to disk.");
                throw;
            }
            */
        }

        private async Task<List<BrowserHistoryEntry>> GetChromeHistoryAsync(CancellationToken ct)
        {
            // ⚠️ DEPRECATED: Batch collection için kullanılıyordu (artık kullanılmıyor)
            await Task.CompletedTask;
            return new List<BrowserHistoryEntry>();
            /*
            var history = new List<BrowserHistoryEntry>();
            var chromePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data\Default\History");

            if (!File.Exists(chromePath))
            {
                _logger.LogDebug("Chrome history file not found: {Path}", chromePath);
                return history;
            }

            var tempPath = await CopyHistoryFileAsync(chromePath, "chrome", ct);
            if (tempPath is null)
            {
                _logger.LogWarning("Failed to copy Chrome history database; skipping.");
                return history;
            }

            try
            {
                await using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly;Cache=Shared");
                await connection.OpenAsync(ct);

                // Calculate timestamp for 24 hours ago
                var yesterday = DateTime.UtcNow.AddDays(-1);
                var yesterdayTicks = (long)((yesterday - ChromeEpoch).TotalMilliseconds * 1000);

                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT 
                        url,
                        title,
                        last_visit_time,
                        visit_count,
                        typed_count
                    FROM urls
                    WHERE url IS NOT NULL
                    AND url != ''
                    AND last_visit_time >= $yesterday_ticks
                    ORDER BY last_visit_time DESC
                    LIMIT $limit";
                command.Parameters.AddWithValue("$limit", _maxEntries);
                command.Parameters.AddWithValue("$yesterday_ticks", yesterdayTicks);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var url = ReadString(reader, 0);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var title = ReadString(reader, 1);
                    var ticks = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                    var visitCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    var typedCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                    var lastVisit = ConvertChromiumTimestamp(ticks);
                    if (lastVisit == DateTime.MinValue)
                    {
                        continue;
                    }

                    history.Add(new BrowserHistoryEntry
                    {
                        Browser = "Chrome",
                        Url = url,
                        Title = title,
                        Domain = ExtractDomain(url),
                        LastVisitTime = lastVisit,
                        VisitCount = visitCount,
                        TypedCount = typedCount,
                        Profile = "Default"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Chrome history");
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            */
        }

        private async Task<List<BrowserHistoryEntry>> GetEdgeHistoryAsync(CancellationToken ct)
        {
            // ⚠️ DEPRECATED: Batch collection için kullanılıyordu (artık kullanılmıyor)
            await Task.CompletedTask;
            return new List<BrowserHistoryEntry>();
            /*
            var history = new List<BrowserHistoryEntry>();
            var edgePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data\Default\History");

            if (!File.Exists(edgePath))
            {
                _logger.LogDebug("Edge history file not found: {Path}", edgePath);
                return history;
            }

            var tempPath = await CopyHistoryFileAsync(edgePath, "edge", ct);
            if (tempPath is null)
            {
                _logger.LogWarning("Failed to copy Edge history database; skipping.");
                return history;
            }

            try
            {
                await using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly;Cache=Shared");
                await connection.OpenAsync(ct);

                // Calculate timestamp for 24 hours ago
                var yesterday = DateTime.UtcNow.AddDays(-1);
                var yesterdayTicks = (long)((yesterday - ChromeEpoch).TotalMilliseconds * 1000);

                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT 
                        url,
                        title,
                        last_visit_time,
                        visit_count,
                        typed_count
                    FROM urls
                    WHERE url IS NOT NULL
                    AND url != ''
                    AND last_visit_time >= $yesterday_ticks
                    ORDER BY last_visit_time DESC
                    LIMIT $limit";
                command.Parameters.AddWithValue("$limit", _maxEntries);
                command.Parameters.AddWithValue("$yesterday_ticks", yesterdayTicks);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var url = ReadString(reader, 0);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var title = ReadString(reader, 1);
                    var ticks = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                    var visitCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    var typedCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                    var lastVisit = ConvertChromiumTimestamp(ticks);
                    if (lastVisit == DateTime.MinValue)
                    {
                        continue;
                    }

                    history.Add(new BrowserHistoryEntry
                    {
                        Browser = "Edge",
                        Url = url,
                        Title = title,
                        Domain = ExtractDomain(url),
                        LastVisitTime = lastVisit,
                        VisitCount = visitCount,
                        TypedCount = typedCount,
                        Profile = "Default"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Edge history");
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            */
        }

        private async Task<List<BrowserHistoryEntry>> GetFirefoxHistoryAsync(CancellationToken ct)
        {
            // ⚠️ DEPRECATED: Batch collection için kullanılıyordu (artık kullanılmıyor)
            await Task.CompletedTask;
            return new List<BrowserHistoryEntry>();
            /*
            var history = new List<BrowserHistoryEntry>();
            var profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");

            if (!Directory.Exists(profilesRoot))
            {
                _logger.LogDebug("Firefox profiles directory not found: {Path}", profilesRoot);
                return history;
            }

            foreach (var profileDir in Directory.GetDirectories(profilesRoot))
            {
                ct.ThrowIfCancellationRequested();

                var profileName = Path.GetFileName(profileDir) ?? "unknown";
                var placesPath = Path.Combine(profileDir, "places.sqlite");
                if (!File.Exists(placesPath))
                {
                    continue;
                }

                var tempPath = await CopyHistoryFileAsync(placesPath, $"firefox_{profileName}", ct);
                if (tempPath is null)
                {
                    _logger.LogWarning("Failed to copy Firefox history database for profile {Profile}.", profileName);
                    continue;
                }

                try
                {
                    await using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly;Cache=Shared");
                    await connection.OpenAsync(ct);

                    // Calculate timestamp for 24 hours ago (Firefox uses microseconds since epoch)
                    var yesterday = DateTime.UtcNow.AddDays(-1);
                    var yesterdayMicroseconds = ((DateTimeOffset)yesterday).ToUnixTimeMilliseconds() * 1000;

                    await using var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT 
                            url,
                            title,
                            last_visit_date,
                            visit_count
                        FROM moz_places
                        WHERE url IS NOT NULL
                        AND url != ''
                        AND last_visit_date IS NOT NULL
                        AND last_visit_date >= $yesterday_microseconds
                        ORDER BY last_visit_date DESC
                        LIMIT $limit";
                    command.Parameters.AddWithValue("$limit", _maxEntries);
                    command.Parameters.AddWithValue("$yesterday_microseconds", yesterdayMicroseconds);

                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var url = ReadString(reader, 0);
                        if (string.IsNullOrWhiteSpace(url))
                        {
                            continue;
                        }

                        var title = ReadString(reader, 1);
                        var raw = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                        var visitCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);

                        var lastVisit = ConvertFirefoxTimestamp(raw);
                        if (lastVisit == DateTime.MinValue)
                        {
                            continue;
                        }

                        history.Add(new BrowserHistoryEntry
                        {
                            Browser = "Firefox",
                            Url = url,
                            Title = title,
                            Domain = ExtractDomain(url),
                            LastVisitTime = lastVisit,
                            VisitCount = visitCount,
                            TypedCount = 0,
                            Profile = profileName
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read Firefox history for profile {Profile}", profileName);
                }
                finally
                {
                    TryDeleteFile(tempPath);
                }
            }

            */
        }

        #region Real-Time Monitoring Methods (ACTIVE)

        /// <summary>
        /// Gerçek zamanlı tarayıcı geçmişi izlemeyi başlat
        /// </summary>
        public async Task StartRealTimeMonitoringAsync()
        {
            if (!_monitoringOptions.EnableRealTimeMonitoring)
            {
                _logger.LogInformation("Real-time browser monitoring is disabled");
                return;
            }

            try
            {
                Directory.CreateDirectory(_activitiesDir);
                _logger.LogInformation("🚀 Real-time browser monitoring started");

                await Task.WhenAll(
                    SetupChromeMonitoringAsync(),
                    SetupEdgeMonitoringAsync(),
                    SetupFirefoxMonitoringAsync(),
                    SetupOperaMonitoringAsync(),
                    SetupBraveMonitoringAsync()
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start real-time browser monitoring");
            }
        }

        private Task SetupChromeMonitoringAsync()
        {
            var historyPath = FindChromeHistoryPath();

            if (historyPath == null)
            {
                _logger.LogDebug("Chrome not found or history file not accessible");
                return Task.CompletedTask;
            }

            SetupBrowserWatcher("Chrome", historyPath, BrowserType.Chromium);
            _logger.LogInformation("✅ Chrome real-time monitoring started: {Path}", Path.GetDirectoryName(historyPath));
            return Task.CompletedTask;
        }

        private string? FindChromeHistoryPath()
        {
            var possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Default\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Profile 1\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Profile 2\History")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private Task SetupEdgeMonitoringAsync()
        {
            var historyPath = FindEdgeHistoryPath();

            if (historyPath == null)
            {
                _logger.LogDebug("Edge not found or history file not accessible");
                return Task.CompletedTask;
            }

            SetupBrowserWatcher("Edge", historyPath, BrowserType.Chromium);
            _logger.LogInformation("✅ Edge real-time monitoring started: {Path}", Path.GetDirectoryName(historyPath));
            return Task.CompletedTask;
        }

        private string? FindEdgeHistoryPath()
        {
            var possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Default\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Profile 1\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Profile 2\History")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private Task SetupFirefoxMonitoringAsync()
        {
            var profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");

            if (!Directory.Exists(profilesRoot))
            {
                _logger.LogDebug("Firefox profiles directory not found");
                return Task.CompletedTask;
            }

            foreach (var profileDir in Directory.GetDirectories(profilesRoot))
            {
                var placesPath = Path.Combine(profileDir, "places.sqlite");
                if (File.Exists(placesPath))
                {
                    SetupBrowserWatcher($"Firefox-{Path.GetFileName(profileDir)}", placesPath, BrowserType.Firefox);
                    _logger.LogInformation("✅ Firefox profile monitoring started: {Profile}", Path.GetFileName(profileDir));
                }
            }

            return Task.CompletedTask;
        }

        private Task SetupOperaMonitoringAsync()
        {
            var historyPath = FindOperaHistoryPath();

            if (historyPath == null)
            {
                _logger.LogDebug("Opera not found or history file not accessible");
                return Task.CompletedTask;
            }

            SetupBrowserWatcher("Opera", historyPath, BrowserType.Chromium);
            _logger.LogInformation("✅ Opera real-time monitoring started: {Path}", Path.GetDirectoryName(historyPath));
            return Task.CompletedTask;
        }

        private string? FindOperaHistoryPath()
        {
            var possiblePaths = new[]
            {
                // Opera Stable
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Opera Software\Opera Stable\History"),
                // Opera GX
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Opera Software\Opera GX Stable\History"),
                // Opera Beta
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Opera Software\Opera Next\History")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private Task SetupBraveMonitoringAsync()
        {
            var historyPath = FindBraveHistoryPath();

            if (historyPath == null)
            {
                _logger.LogDebug("Brave not found or history file not accessible");
                return Task.CompletedTask;
            }

            SetupBrowserWatcher("Brave", historyPath, BrowserType.Chromium);
            _logger.LogInformation("✅ Brave real-time monitoring started: {Path}", Path.GetDirectoryName(historyPath));
            return Task.CompletedTask;
        }

        private string? FindBraveHistoryPath()
        {
            var possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"BraveSoftware\Brave-Browser\User Data\Default\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"BraveSoftware\Brave-Browser\User Data\Profile 1\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"BraveSoftware\Brave-Browser\User Data\Profile 2\History")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private void SetupBrowserWatcher(string browserName, string dbPath, BrowserType browserType)
        {
            try
            {
                var directory = Path.GetDirectoryName(dbPath) ?? throw new InvalidOperationException("Invalid db path");
                var dbFileName = Path.GetFileName(dbPath);

                // Chrome/Edge: Watch both WAL and Journal modes
                // Firefox: Watch WAL only
                var watchFileName = browserType == BrowserType.Chromium
                    ? "History*" // Watch History-wal, History-journal, History itself
                    : "places.sqlite-wal";

                var watcher = new FileSystemWatcher(directory)
                {
                    Filter = watchFileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false
                };

                var browserWatcher = new BrowserWatcher
                {
                    Watcher = watcher
                };

                watcher.Changed += (sender, e) => OnFileChanged(browserName, dbPath, browserType, browserWatcher);
                watcher.Created += (sender, e) => OnFileChanged(browserName, dbPath, browserType, browserWatcher);
                watcher.Error += (sender, e) => _logger.LogError(e.GetException(), "FileSystemWatcher error for {Browser}", browserName);

                _watchers[browserName] = browserWatcher;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to setup watcher for {Browser}", browserName);
            }
        }

        private async void OnFileChanged(string browserName, string dbPath, BrowserType browserType, BrowserWatcher watcher)
        {
            await watcher.Lock.WaitAsync();
            try
            {
                watcher.DebounceCts?.Cancel();
                watcher.DebounceCts = new CancellationTokenSource();
                var debounceToken = watcher.DebounceCts.Token;

                await Task.Delay(_debounceDelayMs, debounceToken);
                if (debounceToken.IsCancellationRequested) return;

                await Task.Delay(_sqliteCommitDelayMs, debounceToken);
                if (debounceToken.IsCancellationRequested) return;

                var since = watcher.LastProcessTime;
                await LogBrowserUrlsAsActivitiesAsync(browserName, dbPath, browserType, since);

                watcher.ErrorCount = 0;
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled, this is normal
            }
            catch (Exception ex)
            {
                watcher.ErrorCount++;
                watcher.LastErrorTime = DateTime.UtcNow;
                _logger.LogError(ex, "❌ Error processing browser history for {Browser} (error #{Count})",
                    browserName, watcher.ErrorCount);
            }
            finally
            {
                watcher.Lock.Release();
            }
        }

        private async Task LogBrowserUrlsAsActivitiesAsync(string browserName, string dbPath, BrowserType browserType, DateTime since)
        {
            try
            {
                var urls = browserType == BrowserType.Chromium
                    ? await GetChromeRecentUrlsAsync(dbPath, since, browserName)
                    : await GetFirefoxRecentUrlsAsync(dbPath, since, browserName);

                if (urls.Count == 0)
                {
                    return;
                }

                var lastUrlTime = urls.Max(u => u.LastVisitTime);
                var watcher = _watchers[browserName];
                watcher.LastProcessTime = lastUrlTime.ToUniversalTime().AddSeconds(1);

                int loggedCount = 0;
                int duplicateCount = 0;

                foreach (var url in urls)
                {
                    if (await IsDuplicateUrlAsync(url.Url, url.LastVisitTime))
                    {
                        duplicateCount++;
                        continue;
                    }

                    // Create BrowserData object instead of ActivityLog
                    var browserData = new BrowserData
                    {
                        Url = url.Url,
                        Title = url.Title,
                        Browser = browserName,
                        VisitTimeUtc = url.LastVisitTime.ToUniversalTime(),
                        BaseUrl = ExtractBaseUrl(url.Url),
                        Username = _username,
                        Domain = _domain,
                        CreatedAtUtc = DateTime.UtcNow
                    };

                    // Save to BrowserDatas table
                    await _databaseService.InsertBrowserDataAsync(browserData);
                    loggedCount++;
                }

                if (loggedCount > 0)
                {
                    _logger.LogInformation("✅ {Browser}: {Count} new URL(s) logged to BrowserDatas", browserName, loggedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log browser URLs for {Browser}", browserName);
            }
        }

        private Task<bool> IsDuplicateUrlAsync(string url, DateTime visitTime)
        {
            try
            {
                var cacheKey = $"{url}:{visitTime:yyyyMMddHHmmss}";
                if (_urlCache.ContainsKey(cacheKey))
                {
                    return Task.FromResult(true);
                }
                _urlCache.TryAdd(cacheKey, DateTime.UtcNow);
                return Task.FromResult(false);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        private async Task WriteToActivityLogAsync(ActivityLog activity)
        {
            try
            {
                var sb = new StringBuilder(512);
                sb.Append("{\"evt\":\"activity\",");
                sb.Append("\"activity_type\":\"").Append(activity.ActivityType).Append("\",");
                sb.Append("\"start\":\"").Append(activity.StartTimeUtc.ToString("O")).Append("\",");
                sb.Append("\"end\":\"").Append(activity.EndTimeUtc.ToString("O")).Append("\",");
                sb.Append("\"duration_ms\":").Append(activity.DurationMs).Append(",");
                sb.Append("\"process\":\"").Append(JsonEncodedText.Encode(activity.ProcessName).Value).Append("\",");
                sb.Append("\"title\":").Append(activity.Title != null ? $"\"{JsonEncodedText.Encode(activity.Title).Value}\"" : "null").Append(",");
                sb.Append("\"username\":\"").Append(activity.Username).Append("\",");
                sb.Append("\"domain\":\"").Append(activity.Domain).Append("\",");
                sb.Append("\"url\":").Append(activity.Url != null ? $"\"{JsonEncodedText.Encode(activity.Url).Value}\"" : "null").Append(",");
                sb.Append("\"browser\":").Append(activity.Browser != null ? $"\"{activity.Browser}\"" : "null");
                sb.Append("}\n");

                var dailyFile = Path.Combine(_activitiesDir, $"{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
                await File.AppendAllTextAsync(dailyFile, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write browser activity log");
            }
        }

        public async Task<List<BrowserHistoryEntry>> GetChromeRecentUrlsAsync(
            string historyPath,
            DateTime since,
            string browserName)
        {
            var entries = new List<BrowserHistoryEntry>();
            string? tempPath = null;

            try
            {
                tempPath = await CopyHistoryFileAsync(historyPath, "chrome-temp", CancellationToken.None);
                if (tempPath == null) return entries;

                var sinceChromium = (long)((since.ToUniversalTime() - ChromeEpoch).TotalMilliseconds * 1000);

                using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
                await connection.OpenAsync();

                var query = @"
                    SELECT
                        u.url,
                        u.title,
                        v.visit_time,
                        v.visit_duration
                    FROM visits v
                    INNER JOIN urls u ON v.url = u.id
                    WHERE v.visit_time > @since
                      AND v.originator_cache_guid IS NULL
                    ORDER BY v.visit_time DESC
                    LIMIT 100";

                using var command = new SqliteCommand(query, connection);
                command.Parameters.AddWithValue("@since", sinceChromium);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var url = reader.GetString(0);
                    var title = reader.IsDBNull(1) ? url : reader.GetString(1);
                    var visitTimeMicro = reader.GetInt64(2);
                    var visitDurationMicro = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                    var visitTime = ConvertChromiumTimestamp(visitTimeMicro);
                    if (visitTime == DateTime.MinValue || visitTime <= since) continue;

                    entries.Add(new BrowserHistoryEntry
                    {
                        Url = url,
                        Title = title,
                        LastVisitTime = visitTime,
                        VisitDuration = visitDurationMicro,
                        Browser = browserName
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Chrome recent URLs");
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            return entries;
        }

        public async Task<List<BrowserHistoryEntry>> GetFirefoxRecentUrlsAsync(
            string historyPath,
            DateTime since,
            string browserName)
        {
            var entries = new List<BrowserHistoryEntry>();
            string? tempPath = null;

            try
            {
                tempPath = await CopyHistoryFileAsync(historyPath, "firefox-temp", CancellationToken.None);
                if (tempPath == null) return entries;

                var sinceFirefox = (long)(since.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds * 1000);

                using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
                await connection.OpenAsync();

                var query = @"
                    SELECT
                        p.url,
                        p.title,
                        v.visit_date,
                        0 as visit_duration
                    FROM moz_historyvisits v
                    INNER JOIN moz_places p ON v.place_id = p.id
                    WHERE v.visit_date > @since
                      AND p.foreign_count = 0
                    ORDER BY v.visit_date DESC
                    LIMIT 100";

                using var command = new SqliteCommand(query, connection);
                command.Parameters.AddWithValue("@since", sinceFirefox);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var url = reader.GetString(0);
                    var title = reader.IsDBNull(1) ? url : reader.GetString(1);
                    var visitTimeMicro = reader.GetInt64(2);

                    var visitTime = ConvertFirefoxTimestamp(visitTimeMicro);
                    if (visitTime == DateTime.MinValue || visitTime <= since) continue;

                    entries.Add(new BrowserHistoryEntry
                    {
                        Url = url,
                        Title = title,
                        LastVisitTime = visitTime,
                        VisitDuration = 0,
                        Browser = browserName
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Firefox recent URLs");
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            return entries;
        }

        public void Dispose()
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
            _cacheCleanupTimer?.Dispose();
        }

        private enum BrowserType
        {
            Chromium,
            Firefox
        }

        #endregion

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static DateTime ConvertChromiumTimestamp(long microseconds)
        {
            if (microseconds <= 0)
            {
                return DateTime.MinValue;
            }

            try
            {
                var milliseconds = microseconds / 1000.0;
                return DateTime.SpecifyKind(ChromeEpoch.AddMilliseconds(milliseconds), DateTimeKind.Utc);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static DateTime ConvertFirefoxTimestamp(long microseconds)
        {
            if (microseconds <= 0)
            {
                return DateTime.MinValue;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).UtcDateTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private async Task<string?> CopyHistoryFileAsync(string sourcePath, string prefix, CancellationToken ct)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"{prefix}_history_{Guid.NewGuid():N}.db");
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Copy(sourcePath, tempPath, true);
                    return tempPath;
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    _logger.LogDebug(ex, "Attempt {Attempt} to copy history file {Source} failed; retrying...", attempt, sourcePath);
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
                {
                    _logger.LogDebug(ex, "Attempt {Attempt} to copy history file {Source} was unauthorized; retrying...", attempt, sourcePath);
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to copy history file {Source}", sourcePath);
                    TryDeleteFile(tempPath);
                    return null;
                }
            }

            TryDeleteFile(tempPath);
            return null;
        }

        private static void TryDeleteFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }

        private static string ExtractDomain(string url)
        {
            try
            {
                return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? uri.Host
                    : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Extract base URL (protocol + domain) from full URL
        /// Example: "https://www.example.com/page" -> "https://www.example.com"
        /// </summary>
        private static string? ExtractBaseUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return $"{uri.Scheme}://{uri.Host}";
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Browser history entry data transfer object.
    /// </summary>
    public class BrowserHistoryEntry
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public DateTime LastVisitTime { get; set; }
        public long VisitDuration { get; set; } // microseconds
        public int VisitCount { get; set; }
        public int TypedCount { get; set; }
        public string Browser { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
    }

    /// <summary>
    /// Options controlling browser history collection.
    /// </summary>
    public class BrowserHistoryOptions
    {
        public const int MinEntries = 100;
        public const int MaxEntries = 10_000;

        public bool EnableChrome { get; set; } = true;
        public bool EnableEdge { get; set; } = true;
        public bool EnableFirefox { get; set; } = true;
        public int MaxHistoryEntries { get; set; } = 1_000;

        public bool EnableAnyBrowser => EnableChrome || EnableEdge || EnableFirefox;

        public IEnumerable<string> GetEnabledBrowsers()
        {
            if (EnableChrome) yield return "Chrome";
            if (EnableEdge) yield return "Edge";
            if (EnableFirefox) yield return "Firefox";
        }

        public static BrowserHistoryOptions FromConfig(PerformanceAgentConfig config)
        {
            return new BrowserHistoryOptions
            {
                EnableChrome = config.EnableChromeHistory,
                EnableEdge = config.EnableEdgeHistory,
                EnableFirefox = config.EnableFirefoxHistory,
                MaxHistoryEntries = config.MaxHistoryEntries
            };
        }
    }
}
