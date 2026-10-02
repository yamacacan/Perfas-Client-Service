using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Sistem sağlık durumu kontrol servisi
    /// Sistemin çalışma durumunu izler ve health check endpoint'i sağlar
    /// </summary>
    public class HealthCheckService
    {
        private readonly ILogger<HealthCheckService> _logger;
        private readonly BrowserHistoryService _browserHistoryService;
        private readonly string _dataDirectory;
        private readonly DateTime _startTime = DateTime.UtcNow;

        public HealthCheckService(
            ILogger<HealthCheckService> logger,
            BrowserHistoryService browserHistoryService,
            string dataDirectory)
        {
            _logger = logger;
            _browserHistoryService = browserHistoryService;
            _dataDirectory = dataDirectory;
        }

        /// <summary>
        /// Sistem sağlık durumunu kontrol et
        /// </summary>
        /// <returns>Health status bilgileri</returns>
        public async Task<HealthStatus> GetHealthStatusAsync()
        {
            try
            {
                var status = new HealthStatus
                {
                    IsHealthy = true,
                    Timestamp = DateTime.UtcNow,
                    Uptime = DateTime.UtcNow - _startTime,
                    Version = GetApplicationVersion(),
                    SystemInfo = GetSystemInfo()
                };

                // Storage health check (replaces Database)
                status.Storage = await CheckStorageHealthAsync();

                // File system health check
                status.FileSystem = CheckFileSystemHealth();

                // Memory health check
                status.Memory = CheckMemoryHealth();

                // Browser watchers health check
                status.BrowserWatchers = CheckBrowserWatchersHealth();

                // Overall health determination
                status.IsHealthy = status.Storage.IsHealthy && 
                                 status.FileSystem.IsHealthy && 
                                 status.Memory.IsHealthy && 
                                 status.BrowserWatchers.IsHealthy;

                return status;
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
        /// Storage sağlık durumunu kontrol et (JSONL)
        /// </summary>
        private async Task<ComponentHealth> CheckStorageHealthAsync()
        {
            await Task.Yield(); // Async uyumluluğu için
            try
            {
                var storagePath = Path.Combine(_dataDirectory, "data");
                if (!Directory.Exists(storagePath))
                {
                    return new ComponentHealth
                    {
                        IsHealthy = false,
                        Message = "Storage directory not found",
                        LastChecked = DateTime.UtcNow
                    };
                }

                // Check writability by creating and deleting a dummy file
                var testFile = Path.Combine(storagePath, $"health_check_{Guid.NewGuid()}.tmp");
                try
                {
                    await File.WriteAllTextAsync(testFile, "test");
                    File.Delete(testFile);
                }
                catch (Exception ex)
                {
                    return new ComponentHealth
                    {
                        IsHealthy = false,
                        Message = $"Storage not writable: {ex.Message}",
                        LastChecked = DateTime.UtcNow,
                        Error = ex.Message
                    };
                }

                var dirs = Directory.GetDirectories(storagePath);
                var files = Directory.GetFiles(storagePath, "*.*", SearchOption.AllDirectories);
                var sizeBytes = files.Sum(f => new FileInfo(f).Length);
                var sizeMB = sizeBytes / (1024.0 * 1024.0);

                return new ComponentHealth
                {
                    IsHealthy = true,
                    Message = $"Storage healthy - {files.Length} files, {sizeMB:F2} MB",
                    LastChecked = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["file_count"] = files.Length,
                        ["size_mb"] = sizeMB,
                        ["sub_directories"] = dirs.Length
                    }
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth
                {
                    IsHealthy = false,
                    Message = $"Storage error: {ex.Message}",
                    LastChecked = DateTime.UtcNow,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// File system sağlık durumunu kontrol et
        /// </summary>
        private ComponentHealth CheckFileSystemHealth()
        {
            try
            {
                var requiredDirs = new[]
                {
                    _dataDirectory,
                    Path.Combine(_dataDirectory, "data"),
                    Path.Combine(_dataDirectory, "data", "activities"),
                    Path.Combine(_dataDirectory, "logs")
                };

                var missingDirs = requiredDirs.Where(dir => !Directory.Exists(dir)).ToList();

                if (missingDirs.Any())
                {
                    return new ComponentHealth
                    {
                        IsHealthy = false,
                        Message = $"Missing directories: {string.Join(", ", missingDirs)}",
                        LastChecked = DateTime.UtcNow
                    };
                }

                // Check disk space
                var drive = new DriveInfo(Path.GetPathRoot(_dataDirectory) ?? "C:");
                var freeSpaceGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                var totalSpaceGB = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                var usagePercent = ((totalSpaceGB - freeSpaceGB) / totalSpaceGB) * 100;

                var isHealthy = freeSpaceGB > 1.0; // At least 1GB free space

                return new ComponentHealth
                {
                    IsHealthy = isHealthy,
                    Message = isHealthy ? 
                        $"File system healthy - {freeSpaceGB:F2} GB free" : 
                        $"Low disk space - {freeSpaceGB:F2} GB free",
                    LastChecked = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["free_space_gb"] = freeSpaceGB,
                        ["total_space_gb"] = totalSpaceGB,
                        ["usage_percent"] = usagePercent
                    }
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth
                {
                    IsHealthy = false,
                    Message = $"File system error: {ex.Message}",
                    LastChecked = DateTime.UtcNow,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Memory sağlık durumunu kontrol et
        /// </summary>
        private ComponentHealth CheckMemoryHealth()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var workingSetMB = process.WorkingSet64 / (1024.0 * 1024.0);
                var privateMemoryMB = process.PrivateMemorySize64 / (1024.0 * 1024.0);

                // Get system memory info
                var totalMemory = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                var gen0Collections = GC.CollectionCount(0);
                var gen1Collections = GC.CollectionCount(1);
                var gen2Collections = GC.CollectionCount(2);

                var isHealthy = workingSetMB < 500; // Less than 500MB working set

                return new ComponentHealth
                {
                    IsHealthy = isHealthy,
                    Message = isHealthy ? 
                        $"Memory healthy - {workingSetMB:F2} MB working set" : 
                        $"High memory usage - {workingSetMB:F2} MB working set",
                    LastChecked = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["working_set_mb"] = workingSetMB,
                        ["private_memory_mb"] = privateMemoryMB,
                        ["gc_memory_mb"] = totalMemory,
                        ["gc_gen0_collections"] = gen0Collections,
                        ["gc_gen1_collections"] = gen1Collections,
                        ["gc_gen2_collections"] = gen2Collections
                    }
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth
                {
                    IsHealthy = false,
                    Message = $"Memory check error: {ex.Message}",
                    LastChecked = DateTime.UtcNow,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Browser watchers sağlık durumunu kontrol et
        /// </summary>
        private ComponentHealth CheckBrowserWatchersHealth()
        {
            try
            {
                // Bu method BrowserHistoryService'den watcher bilgilerini alacak
                // Şimdilik basit bir kontrol yapıyoruz
                var chromePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\User Data\Default\History");
                var edgePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Edge\User Data\Default\History");
                var firefoxPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Mozilla\Firefox\Profiles");

                var availableBrowsers = new List<string>();
                if (File.Exists(chromePath)) availableBrowsers.Add("Chrome");
                if (File.Exists(edgePath)) availableBrowsers.Add("Edge");
                if (Directory.Exists(firefoxPath)) availableBrowsers.Add("Firefox");

                var isHealthy = availableBrowsers.Any();

                return new ComponentHealth
                {
                    IsHealthy = isHealthy,
                    Message = isHealthy ? 
                        $"Browser watchers healthy - {string.Join(", ", availableBrowsers)} available" : 
                        "No browser history files found",
                    LastChecked = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["available_browsers"] = availableBrowsers,
                        ["chrome_exists"] = File.Exists(chromePath),
                        ["edge_exists"] = File.Exists(edgePath),
                        ["firefox_exists"] = Directory.Exists(firefoxPath)
                    }
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth
                {
                    IsHealthy = false,
                    Message = $"Browser watchers error: {ex.Message}",
                    LastChecked = DateTime.UtcNow,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Uygulama versiyonunu al
        /// </summary>
        private string GetApplicationVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Sistem bilgilerini al
        /// </summary>
        private SystemInfo GetSystemInfo()
        {
            try
            {
                return new SystemInfo
                {
                    MachineName = Environment.MachineName,
                    UserName = Environment.UserName,
                    OSVersion = Environment.OSVersion.VersionString,
                    ProcessorCount = Environment.ProcessorCount,
                    WorkingSet = Environment.WorkingSet,
                    Is64BitProcess = Environment.Is64BitProcess,
                    Is64BitOperatingSystem = Environment.Is64BitOperatingSystem
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get system info");
                return new SystemInfo
                {
                    MachineName = "Unknown",
                    UserName = "Unknown",
                    OSVersion = "Unknown"
                };
            }
        }
    }

    /// <summary>
    /// Health status data model
    /// </summary>
    public class HealthStatus
    {
        public bool IsHealthy { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Uptime { get; set; }
        public string Version { get; set; } = string.Empty;
        public SystemInfo SystemInfo { get; set; } = new();
        public ComponentHealth Storage { get; set; } = new(); // Replaced Database with Storage
        public ComponentHealth FileSystem { get; set; } = new();
        public ComponentHealth Memory { get; set; } = new();
        public ComponentHealth BrowserWatchers { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>
    /// Component health data model
    /// </summary>
    public class ComponentHealth
    {
        public bool IsHealthy { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime LastChecked { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>
    /// System info data model
    /// </summary>
    public class SystemInfo
    {
        public string MachineName { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string OSVersion { get; set; } = string.Empty;
        public int ProcessorCount { get; set; }
        public long WorkingSet { get; set; }
        public bool Is64BitProcess { get; set; }
        public bool Is64BitOperatingSystem { get; set; }
    }
}
