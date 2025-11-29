using System;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Kullanıcı bilgisi toplama servisi - temel kullanıcı bilgilerini toplar
    /// Kullanıcı adı, domain ve SID bilgilerini toplar
    /// </summary>
    public class UserInfoService
    {
        // Servis bağımlılıkları
        private readonly ILogger<UserInfoService> _logger;
        private readonly string _systemDir; // Sistem bilgilerinin kaydedileceği dizin

        /// <summary>
        /// Constructor - servis bağımlılıklarını alır
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="systemDir">Sistem bilgilerinin dizini</param>
        public UserInfoService(ILogger<UserInfoService> logger, string systemDir)
        {
            _logger = logger;
            _systemDir = systemDir;
        }

        /// <summary>
        /// Kullanıcı bilgilerini topla
        /// </summary>
        /// <param name="runId">Çalışma oturumu ID'si</param>
        /// <param name="ct">İptal token'ı</param>
        public async Task CollectUserInfoAsync(Guid runId, CancellationToken ct)
        {
            try
            {
                // Sistem dizinini oluştur
                Directory.CreateDirectory(_systemDir);

                // Kullanıcı bilgilerini topla
                var identity = WindowsIdentity.GetCurrent();

                var userInfo = new
                {
                    username = Environment.UserName ?? "Unknown",
                    domain = Environment.UserDomainName ?? "Unknown",
                    user_sid = identity?.User?.Value ?? "Unknown"
                };

                var outputPath = Path.Combine(_systemDir, "user-info.json");
                var json = JsonSerializer.Serialize(userInfo, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outputPath, json, ct);

                _logger.LogInformation("User information saved to {Path}", outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect user information");
                throw;
            }
        }
    }
}
