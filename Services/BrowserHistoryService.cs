using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // Servis bağımlılıkları
        private readonly ILogger<BrowserHistoryService> _logger;
        private readonly string _dataDirectory; // Veri dizini

        // Real-time monitoring fields
        private readonly string _activitiesDir; // Activity logların kaydedileceği dizin
        private readonly string _username; // Kullanıcı adı
        private readonly string _domain; // Domain adı
        private readonly BrowserHistoryMonitoringOptions _monitoringOptions; // Monitoring yapılandırması
        private readonly Dictionary<string, BrowserWatcher> _watchers = []; // Browser watcher'lar
        private readonly IBrowserStore _storageService; // JSONL depolama servisi
        private readonly IBrowserHistoryReader _reader; // Tarayıcı geçmiş DB okuyucu

        // Optimizasyon: In-memory duplicate cache (100x daha hızlı)
        private readonly ConcurrentDictionary<string, DateTime> _urlCache = new();
        private readonly Timer? _cacheCleanupTimer;

        // Optimizasyon: Cached configuration values
        private readonly int _debounceDelayMs;
        private readonly int _sqliteCommitDelayMs;

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
            public int ErrorCount { get; set; } = 0;
            public DateTime LastErrorTime { get; set; } = DateTime.MinValue;

            public void Dispose()
            {
                Watcher?.Dispose();
                DebounceCts?.Dispose();
                Lock?.Dispose();
            }
        }

        public BrowserHistoryService(
            ILogger<BrowserHistoryService> logger,
            string dataDirectory,
            IBrowserStore storageService,
            IBrowserHistoryReader reader,
            string? activitiesDir = null,
            string? username = null,
            string? domain = null,
            BrowserHistoryMonitoringOptions? monitoringOptions = null)
        {
            _logger = logger;
            _dataDirectory = dataDirectory;
            _storageService = storageService;
            _reader = reader;

            // Real-time monitoring setup
            _activitiesDir = activitiesDir ?? Path.Combine(dataDirectory, "activities");
            _username = username ?? Environment.UserName ?? "Unknown";
            _domain = domain ?? Environment.UserDomainName ?? "Unknown";
            _monitoringOptions = monitoringOptions ?? new BrowserHistoryMonitoringOptions();

            // Optimizasyon: Cache configuration values
            _debounceDelayMs = _monitoringOptions.DebounceDelayMs;
            _sqliteCommitDelayMs = _monitoringOptions.SqliteCommitDelayMs;

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

        /// <summary>
        /// Chromium tabanlı tarayıcılar için tüm profilleri dinamik olarak bul
        /// Default, Profile 1/2/3, Guest Profile, System Profile ve özel isimli profilleri destekler
        /// </summary>
        private List<string> FindChromiumProfiles(string userDataPath, bool checkRootLevel = false)
        {
            var profiles = new List<string>();

            if (!Directory.Exists(userDataPath))
                return profiles;

            try
            {
                // Opera ve bazı tarayıcılar için: Kök seviyede History kontrolü
                if (checkRootLevel)
                {
                    var rootHistory = Path.Combine(userDataPath, "History");
                    if (File.Exists(rootHistory))
                    {
                        profiles.Add(rootHistory);
                        _logger.LogDebug("Found root-level History at {Path}", userDataPath);
                    }
                }

                // Tüm alt klasörleri tara ve History dosyası içerenleri bul
                foreach (var directory in Directory.GetDirectories(userDataPath))
                {
                    var dirName = Path.GetFileName(directory);

                    // Sistem/cache klasörlerini atla
                    if (IsSystemDirectory(dirName))
                        continue;

                    var historyPath = Path.Combine(directory, "History");
                    if (File.Exists(historyPath))
                    {
                        profiles.Add(historyPath);
                        _logger.LogDebug("Found profile with History: {Profile}", dirName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan Chromium profiles at {Path}", userDataPath);
            }

            return profiles;
        }

        /// <summary>
        /// Profil olmayan sistem/cache klasörlerini tespit et
        /// Bu klasörler Chromium tarafından cache ve metadata için kullanılır
        /// </summary>
        private static bool IsSystemDirectory(string dirName)
        {
            // Bilinen sistem/cache klasörleri - History dosyası içermezler
            // Bu liste Chromium'un standart yapısına dayanır
            var systemDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Crash & metrics
                "Crashpad", "CrashReports", "BrowserMetrics",
                
                // Security & certificates
                "CertificateRevocation", "CertificateTransparency", "SSLErrorAssistant",
                "Safe Browsing", "SafetyTips", "TrustTokenKeyCommitments",
                
                // Cache & temporary
                "ShaderCache", "GrShaderCache", "GPUCache", "Cache", "Code Cache",
                "component_crx_cache", "pnacl", "PepperFlash",
                
                // Optimization & ML
                "optimization_guide_model_store", "OptimizationHints", "segmentation_platform",
                
                // Extensions & downloads
                "Webstore Downloads", "Extensions Temp", "FileTypePolicies",
                
                // Sync & network
                "Sync Data", "Sync DataBackup", "Network", "Network Persistent State",
                
                // Media & fonts
                "Fonts", "FontLookupTableCache", "MediaFoundation",
                "WidevineCdm", "MEIPreload",
                
                // Other system folders
                "ThirdPartyModuleList64",
                "TpcdMetadata",
                "ZxcvbnData",
                "AutofillStates",
                "Subresource Filter",
                "Local Storage", "IndexedDB", "Session Storage", "Service Worker",
                "BudgetDatabase",
                "Crowd Deny",
                "Feature Engagement Tracker",
                "Floc",
                "GCM Store",
                "DIPS", "PrivateAggregation",
                "Local Traces", "RecoveryImproved",
                "SiteCharacteristicsDatabase",
                "Storage",
                "Translate Ranker Model",
                "OriginTrials"
            };

            return systemDirs.Contains(dirName);
        }

        private Task SetupChromeMonitoringAsync()
        {
            var userDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data");

            var profiles = FindChromiumProfiles(userDataPath);

            if (profiles.Count == 0)
            {
                _logger.LogDebug("Chrome not found or no profiles");
                return Task.CompletedTask;
            }

            foreach (var profilePath in profiles)
            {
                var profileName = Path.GetFileName(Path.GetDirectoryName(profilePath));
                SetupBrowserWatcher($"Chrome-{profileName}", profilePath, BrowserType.Chromium);
                _logger.LogInformation("✅ Chrome profile monitoring started: {Profile}", profileName);
            }

            return Task.CompletedTask;
        }

        private Task SetupEdgeMonitoringAsync()
        {
            var userDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data");

            var profiles = FindChromiumProfiles(userDataPath);

            if (profiles.Count == 0)
            {
                _logger.LogDebug("Edge not found or no profiles");
                return Task.CompletedTask;
            }

            foreach (var profilePath in profiles)
            {
                var profileName = Path.GetFileName(Path.GetDirectoryName(profilePath));
                SetupBrowserWatcher($"Edge-{profileName}", profilePath, BrowserType.Chromium);
                _logger.LogInformation("✅ Edge profile monitoring started: {Profile}", profileName);
            }

            return Task.CompletedTask;
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
            var variants = new[]
            {
                (Name: "Opera Stable", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera Stable")),
                (Name: "Opera GX", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera GX Stable")),
                (Name: "Opera Beta", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera Next"))
            };

            foreach (var variant in variants)
            {
                // Opera için checkRootLevel = true çünkü History dosyası kök seviyede olabilir
                var profiles = FindChromiumProfiles(variant.Path, checkRootLevel: true);
                foreach (var profilePath in profiles)
                {
                    // Kök seviyedeki History için "Root" kullan, alt klasör için klasör adını kullan
                    var parentDir = Path.GetDirectoryName(profilePath);
                    var isRootLevel = parentDir != null &&
                                      Path.GetFileName(parentDir).Equals(Path.GetFileName(variant.Path), StringComparison.OrdinalIgnoreCase);
                    var profileName = isRootLevel ? "Root" : Path.GetFileName(parentDir);

                    SetupBrowserWatcher($"{variant.Name}-{profileName}", profilePath, BrowserType.Chromium);
                    _logger.LogInformation("✅ {Variant} profile monitoring started: {Profile}", variant.Name, profileName);
                }
            }

            return Task.CompletedTask;
        }

        private Task SetupBraveMonitoringAsync()
        {
            var userDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"BraveSoftware\Brave-Browser\User Data");

            var profiles = FindChromiumProfiles(userDataPath);

            if (profiles.Count == 0)
            {
                _logger.LogDebug("Brave not found or no profiles");
                return Task.CompletedTask;
            }

            foreach (var profilePath in profiles)
            {
                var profileName = Path.GetFileName(Path.GetDirectoryName(profilePath));
                SetupBrowserWatcher($"Brave-{profileName}", profilePath, BrowserType.Chromium);
                _logger.LogInformation("✅ Brave profile monitoring started: {Profile}", profileName);
            }

            return Task.CompletedTask;
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
                    ? await _reader.GetChromeRecentUrlsAsync(dbPath, since, browserName)
                    : await _reader.GetFirefoxRecentUrlsAsync(dbPath, since, browserName);

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

                    // Create BrowserData object
                    var browserData = new BrowserData
                    {
                        Url = url.Url,
                        Title = url.Title,
                        Browser = browserName,
                        VisitTimeUtc = url.LastVisitTime.ToUniversalTime(),
                        BaseUrl = ExtractBaseUrl(url.Url),
                        Username = _username,
                        Domain = _domain,
                        UserSid = SystemIdentityHelper.GetCurrentUserSid(),
                        CreatedAtUtc = DateTime.UtcNow
                    };

                    // Save to JSONL storage
                    await _storageService.SaveBrowserDataAsync(browserData);
                    loggedCount++;
                }

                if (loggedCount > 0)
                {
                    _logger.LogInformation("✅ {Browser}: {Count} new URL(s) logged to JSONL", browserName, loggedCount);
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

        private static string ExtractBaseUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return Uri.UnescapeDataString(uri.GetLeftPart(UriPartial.Path));
                }
                return url;
            }
            catch
            {
                return url;
            }
        }

    }

    /// <summary>
    /// Browser history entry model
    /// </summary>
    public class BrowserHistoryEntry
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime LastVisitTime { get; set; }
        public long VisitDuration { get; set; }
        public string Browser { get; set; } = string.Empty;
    }
}
