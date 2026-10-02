using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Konfigürasyon servisi - doğrulama ve güvenlik iyileştirmeleri ile
    /// Uygulama ayarlarını okur, doğrular ve güvenli dizin yolları sağlar
    /// </summary>
    public class ConfigurationService
    {
        // Servis bağımlılıkları
        private readonly ILogger<ConfigurationService> _logger;
        private readonly IConfiguration _configuration;
        //
        /// <summary>
        /// Constructor - servis bağımlılıklarını alır
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="configuration">Konfigürasyon servisi</param>
        public ConfigurationService(ILogger<ConfigurationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// Doğrulanmış konfigürasyonu al
        /// </summary>
        /// <returns>Doğrulanmış ve düzeltilmiş konfigürasyon</returns>
        public PerformanceAgentConfig GetValidatedConfig()
        {
            // Konfigürasyonu oku
            var config = _configuration.GetSection("PerformanceAgent").Get<PerformanceAgentConfig>() ?? new PerformanceAgentConfig();
            
            // Konfigürasyonu doğrula ve düzelt
            ValidateAndFixConfig(config);
            
            return config;
        }

        /// <summary>
        /// Güvenli veri dizini yolu al
        /// </summary>
        /// <param name="configuredPath">Konfigürasyonda belirtilen yol</param>
        /// <returns>Güvenli ve erişilebilir dizin yolu</returns>
        public string GetSafeDataDirectory(string? configuredPath = null)
        {
            try
            {
                string dataDir;
                
                // Konfigüre edilmiş yol sağlanmışsa ve geçerliyse kullan
                if (!string.IsNullOrWhiteSpace(configuredPath) && IsValidPath(configuredPath))
                {
                    dataDir = Path.GetFullPath(configuredPath);
                }
                else
                {
                    // Ortam tabanlı yola geri dön
                    var basePath = GetEnvironmentBasedPath();
                    dataDir = Path.Combine(basePath, "PerformanceAgent");
                }
                
                // Dizini oluştur ve yazma izinlerini kontrol et
                EnsureDirectoryWithPermissions(dataDir);
                
                _logger.LogInformation("Using data directory: {Path}", dataDir);
                return dataDir;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to determine data directory, using temp path");
                var tempDir = Path.Combine(Path.GetTempPath(), "PerformanceAgent");
                try
                {
                    EnsureDirectoryWithPermissions(tempDir);
                }
                catch
                {
                    // Temp path should always be writable, but just in case
                    Directory.CreateDirectory(tempDir);
                }
                return tempDir;
            }
        }

        /// <summary>
        /// Konfigürasyonu doğrula ve düzelt
        /// </summary>
        /// <param name="config">Konfigürasyon nesnesi</param>
        private void ValidateAndFixConfig(PerformanceAgentConfig config)
        {
            // Poll interval kontrolü
            if (config.PollIntervalMs < 1000)
            {
                config.PollIntervalMs = 2000;
                _logger.LogWarning("Poll interval too low, set to 2000ms");
            }

            // Apps refresh interval kontrolü
            if (config.AppsRefreshIntervalHours < 1)
            {
                config.AppsRefreshIntervalHours = 24;
                _logger.LogWarning("Apps refresh interval too low, set to 24 hours");
            }

            // Browser history refresh interval kontrolü
            if (config.BrowserHistoryRefreshIntervalHours < 1)
            {
                config.BrowserHistoryRefreshIntervalHours = 24;
                _logger.LogWarning("Browser history refresh interval too low, set to 24 hours");
            }

            // Max history entries kontrolü
            if (config.MaxHistoryEntries < 100)
            {
                config.MaxHistoryEntries = 1000;
                _logger.LogWarning("Max history entries too low, set to 1000");
            }
        }

        /// <summary>
        /// Yol geçerliliğini kontrol et
        /// </summary>
        /// <param name="path">Kontrol edilecek yol</param>
        /// <returns>Geçerli mi?</returns>
        private static bool IsValidPath(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                return !string.IsNullOrWhiteSpace(fullPath) && 
                       !fullPath.Contains("..") && 
                       !fullPath.Contains("?") &&
                       fullPath.Length < 260;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Ortam tabanlı yol al
        /// Shared Service ve Agent erişimi için CommonApplicationData (ProgramData) kullanılır
        /// </summary>
        /// <returns>Ortam tabanlı yol</returns>
        private static string GetEnvironmentBasedPath()
        {
            // Servis ve Agent'ın aynı veritabanını okuyabilmesi için ortak alan (ProgramData) kullanılmalı
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(commonAppData))
            {
                return commonAppData;
            }

            // Yedek olarak AppData
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                return appData;
            }

            // Sonra LocalAppData
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                return localAppData;
            }

            // Son çare olarak temp
            return Path.GetTempPath();
        }

        /// <summary>
        /// Dizini oluştur ve yazma izinlerini ayarla
        /// Windows Service ve Agent'ın aynı veritabanına yazabilmesi için gerekli
        /// </summary>
        /// <param name="directoryPath">Oluşturulacak dizin yolu</param>
        private void EnsureDirectoryWithPermissions(string directoryPath)
        {
            try
            {
                // Dizin yoksa oluştur
                if (!Directory.Exists(directoryPath))
                {
                    var dirInfo = Directory.CreateDirectory(directoryPath);
                    _logger.LogInformation("Created directory: {Path}", directoryPath);
                    
                    // Windows'ta ACL ayarla - Everyone'a FullControl ver
                    try
                    {
                        SetDirectoryPermissions(directoryPath);
                    }
                    catch (Exception aclEx)
                    {
                        _logger.LogWarning(aclEx, "Could not set ACL permissions on directory, may cause issues for Windows Service");
                    }
                }
                else
                {
                    // Dizin var, yazılabilir mi kontrol et
                    var testFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid():N}");
                    try
                    {
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _logger.LogWarning("Directory {Path} exists but is not writable, attempting to fix permissions", directoryPath);
                        try
                        {
                            SetDirectoryPermissions(directoryPath);
                        }
                        catch (Exception aclEx)
                        {
                            _logger.LogError(aclEx, "Failed to fix directory permissions");
                            throw;
                        }
                    }
                    catch (IOException)
                    {
                        // IO exception during test write might indicate other issues
                        _logger.LogDebug("IO exception during directory write test, directory may be usable");
                    }
                }
                
                // Veritabanı dosyaları varsa read-only flag'i kaldır
                RemoveReadOnlyFromDatabaseFiles(directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring directory with permissions: {Path}", directoryPath);
                throw;
            }
        }

        /// <summary>
        /// Dizin için yazma izinlerini ACL ile ayarla
        /// </summary>
        private void SetDirectoryPermissions(string directoryPath)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                var security = dirInfo.GetAccessControl();
                
                // En az yetki: SYSTEM + Administrators tam yetki, Users yalnızca Modify.
                // (Everyone/FullControl verilmez; ACL devralması korunur.)
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                foreach (var (sid, rights) in new[]
                {
                    (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                    (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                    (WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify)
                })
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        new SecurityIdentifier(sid, null), rights, inherit,
                        PropagationFlags.None, AccessControlType.Allow));
                }

                dirInfo.SetAccessControl(security);
                _logger.LogInformation("Set ACL permissions on directory: {Path}", directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set ACL permissions, running as standard user may have limitations");
            }
        }

        /// <summary>
        /// Veritabanı dosyalarından read-only flag'i kaldır
        /// </summary>
        private void RemoveReadOnlyFromDatabaseFiles(string directoryPath)
        {
            try
            {
                var dbFiles = new[] { "activities_v2.db", "activities_v2.db-wal", "activities_v2.db-shm" };
                foreach (var dbFile in dbFiles)
                {
                    var filePath = Path.Combine(directoryPath, dbFile);
                    if (File.Exists(filePath))
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (fileInfo.IsReadOnly)
                        {
                            fileInfo.IsReadOnly = false;
                            _logger.LogInformation("Removed read-only attribute from: {Path}", filePath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove read-only attributes from database files");
            }
        }
    }

    /// <summary>
    /// Performance Agent konfigürasyon sınıfı
    /// </summary>
    public class PerformanceAgentConfig
    {
        public int PollIntervalMs { get; set; } = 2000;
        public int AppsRefreshIntervalHours { get; set; } = 24;
        public int DataRetentionDays { get; set; } = 30;
        public bool EnableUwpCollection { get; set; } = true;
        public bool EnableWin32Collection { get; set; } = true;
        public string DataDirectory { get; set; } = "";
        public bool EnableConsoleOutput { get; set; } = true;
        public bool EnableDebugLogging { get; set; } = false;
        public bool EnableBrowserHistoryCollection { get; set; } = true;
        public int BrowserHistoryRefreshIntervalHours { get; set; } = 24;
        public int MaxHistoryEntries { get; set; } = 1000;
        public bool EnableChromeHistory { get; set; } = true;
        public bool EnableEdgeHistory { get; set; } = true;
        public bool EnableFirefoxHistory { get; set; } = true;
    }
}