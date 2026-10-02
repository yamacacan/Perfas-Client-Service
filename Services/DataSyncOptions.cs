using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>Senkronizasyon ayarları (tek sorumluluk: ayar modeli).</summary>
    public sealed class DataSyncOptions
    {
        public bool Enabled { get; init; } = true;
        public int SyncIntervalMinutes { get; init; } = 10;
        public int BatchSize { get; init; } = 100;
        public bool DeleteAfterSync { get; init; } = true;
        public string ApiUrl { get; init; } = "";
        public string ApiToken { get; init; } = "";
        public string AesKey { get; init; } = "";

        public bool IsConfigured =>
            Enabled && !string.IsNullOrWhiteSpace(ApiUrl) &&
            !string.IsNullOrWhiteSpace(ApiToken) && !string.IsNullOrWhiteSpace(AesKey);
    }

    /// <summary>
    /// Ayarları yükler. Gizli değerler (URL/token/anahtar) önce HKLM registry'den (MSI yazar),
    /// sonra PERFAS_* ortam değişkenlerinden, en son yapılandırmadan okunur. Repoda gizli değer tutulmaz.
    /// </summary>
    public static class DataSyncOptionsLoader
    {
        private const string RegistryPath = @"SOFTWARE\Perfas\PerformanceAgent";

        public static DataSyncOptions Load(IConfiguration config, ILogger logger)
        {
            var section = config.GetSection("DataSync");
            return new DataSyncOptions
            {
                Enabled = section.GetValue("Enabled", true),
                SyncIntervalMinutes = Math.Max(1, section.GetValue("SyncIntervalMinutes", 10)),
                BatchSize = Math.Clamp(section.GetValue("BatchSize", 100), 1, 1000),
                DeleteAfterSync = section.GetValue("DeleteAfterSync", true),
                ApiUrl = Secret("ApiUrl", section, logger),
                ApiToken = Secret("ApiToken", section, logger),
                AesKey = Secret("AesKey", section, logger)
            };
        }

        private static string Secret(string name, IConfigurationSection section, ILogger logger)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryPath);
                var v = key?.GetValue(name)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Registry okunamadı: {Name}", name);
            }

            var env = Environment.GetEnvironmentVariable($"PERFAS_{name.ToUpperInvariant()}");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            return section[name]?.Trim() ?? "";
        }
    }
}
