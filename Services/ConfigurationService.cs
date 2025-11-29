using System;
using System.IO;
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
                // Konfigüre edilmiş yol sağlanmışsa ve geçerliyse kullan
                if (!string.IsNullOrWhiteSpace(configuredPath) && IsValidPath(configuredPath))
                {
                    return Path.GetFullPath(configuredPath);
                }

                // Ortam tabanlı yola geri dön
                var basePath = GetEnvironmentBasedPath();
                var dataDir = Path.Combine(basePath, "PerformanceAgent");
                
                _logger.LogInformation("Using data directory: {Path}", dataDir);
                return dataDir;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to determine data directory, using temp path");
                return Path.Combine(Path.GetTempPath(), "PerformanceAgent");
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
        /// </summary>
        /// <returns>Ortam tabanlı yol</returns>
        private static string GetEnvironmentBasedPath()
        {
            // Önce AppData kullanmaya çalış
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