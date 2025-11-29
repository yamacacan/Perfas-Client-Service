using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Aktivite takip servisi - pencere izleme için
    /// Kullanıcının aktif pencere değişikliklerini takip eder ve oturum bilgilerini kaydeder
    /// </summary>
    public class ActivityTrackingService
    {
        // Servis bağımlılıkları
        private readonly ILogger<ActivityTrackingService> _logger;
        private readonly string _activitiesDir; // Aktivite loglarının dizini
        private readonly bool _enableConsoleOutput; // Konsol çıktısı aktif mi?
        private readonly ActivityDatabaseService _databaseService; // SQLite veritabanı servisi

        // Aktivite durumu
        private int _currentPid = -1; // Mevcut process ID
        private string _currentProcess = ""; // Mevcut process adı
        private string _currentTitle = ""; // Mevcut pencere başlığı
        private DateTimeOffset _sessionStart = DateTimeOffset.MinValue; // Oturum başlangıç zamanı

        // Browser tracking
        private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
        };

        // Görünmez format karakterlerini temizlemek için regex
        private static readonly Regex FormatChars = new(@"\p{Cf}", RegexOptions.Compiled);

        public ActivityTrackingService(
            ILogger<ActivityTrackingService> logger, 
            string activitiesDir,
            ActivityDatabaseService databaseService,
            bool enableConsoleOutput = true)
        {
            _logger = logger;
            _activitiesDir = activitiesDir;
            _databaseService = databaseService;
            _enableConsoleOutput = enableConsoleOutput;
        }

        private static bool IsBrowser(string processName)
        {
            return BrowserProcessNames.Any(browser => processName.Contains(browser, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Process window change - SADECE PENCERE DEĞİŞİKLİĞİNDE KAYIT
        /// </summary>
        public async Task ProcessWindowChangeAsync(int pid, string processName, string title, string username, string domain, CancellationToken ct)
        {
            try
            {
                if (_sessionStart == DateTimeOffset.MinValue)
                {
                    // First window
                    _currentPid = pid;
                    _currentProcess = processName ?? "";
                    _currentTitle = title ?? "";
                    _sessionStart = DateTimeOffset.Now;
                }
                else if (pid != _currentPid || !string.Equals(title, _currentTitle, StringComparison.Ordinal))
                {
                    // Window changed - log previous activity
                    await LogActivityAsync(DateTimeOffset.Now, username, domain, ct);

                    // Start new session
                    _currentPid = pid;
                    _currentProcess = processName ?? "";
                    _currentTitle = title ?? "";
                    _sessionStart = DateTimeOffset.Now;
                }
                // Aynı pencere - hiçbir şey yapma (sürekli kayıt yok)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process window change");
                throw;
            }
        }

        /// <summary>
        /// Log current activity and reset session
        /// </summary>
        public async Task LogCurrentActivityAsync(string username, string domain, CancellationToken ct)
        {
            try
            {
                if (_sessionStart != DateTimeOffset.MinValue)
                {
                    await LogActivityAsync(DateTimeOffset.Now, username, domain, ct);
                    _sessionStart = DateTimeOffset.MinValue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log current activity");
                throw;
            }
        }

        /// <summary>
        /// Log activity to database - Window activity kaydeder
        /// </summary>
        private async Task LogActivityAsync(DateTimeOffset endTime, string username, string domain, CancellationToken ct)
        {
            try
            {
                var durationMs = (long)(endTime - _sessionStart).TotalMilliseconds;
                var title = CleanString(_currentTitle);
                var process = CleanString(_currentProcess);

                var userSid = GetCurrentUserSid();
                var motherboardUuid = GetMotherboardUuid();
                
                // Debug: Log values
                _logger.LogDebug("Creating ActivityLog - UserSid: '{UserSid}', MotherboardUuid: '{Uuid}'", 
                    userSid, motherboardUuid);

                // SQLite veritabanına kaydet
                var activity = new ActivityLog
                {
                    Evt = "activity", // Event tipi
                    ActivityType = "window", // Window activity
                    ProcessName = process,
                    Title = title,
                    StartTimeUtc = _sessionStart.UtcDateTime,
                    EndTimeUtc = endTime.UtcDateTime,
                    DurationMs = durationMs,
                    Username = username,
                    Domain = domain,
                    UserSid = userSid,
                    MotherboardUuid = motherboardUuid
                };

                await _databaseService.InsertActivityAsync(activity);

                // JSON dosyasına da yaz (backup için)
                await WriteToActivityLogAsync(activity, ct);
                
                _logger.LogInformation("📝 Window activity kaydedildi: {Process} - {Title} ({Duration}ms)", 
                    process, title, durationMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log activity");
                throw;
            }
        }


        /// <summary>
        /// Write activity to log file (backup)
        /// </summary>
        private async Task WriteToActivityLogAsync(ActivityLog activity, CancellationToken ct)
        {
            try
            {
                // Optimizasyon: StringBuilder kullan (JsonSerializer'dan daha hızlı)
                var sb = new StringBuilder(384); // Pre-allocate
                sb.Append("{\"evt\":\"activity\",");
                sb.Append("\"activity_type\":\"").Append(activity.ActivityType).Append("\",");
                sb.Append("\"start\":\"").Append(activity.StartTimeUtc.ToString("O")).Append("\",");
                sb.Append("\"end\":\"").Append(activity.EndTimeUtc.ToString("O")).Append("\",");
                sb.Append("\"duration_ms\":").Append(activity.DurationMs).Append(",");
                sb.Append("\"process\":\"").Append(JsonEncodedText.Encode(activity.ProcessName).Value).Append("\",");
                sb.Append("\"title\":").Append(activity.Title != null ? $"\"{JsonEncodedText.Encode(activity.Title).Value}\"" : "null").Append(",");
                sb.Append("\"username\":\"").Append(activity.Username).Append("\",");
                sb.Append("\"domain\":\"").Append(activity.Domain).Append("\"");
                sb.Append("}\n");

                var dailyFile = Path.Combine(_activitiesDir, $"{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
                await File.AppendAllTextAsync(dailyFile, sb.ToString(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write activity log");
                throw;
            }
        }

        /// <summary>
        /// Clean string from invisible characters
        /// </summary>
        private static string CleanString(string? s)
        {
            return string.IsNullOrEmpty(s) ? (s ?? "") : FormatChars.Replace(s, "");
        }

        /// <summary>
        /// Get current user SID
        /// </summary>
        private static string GetCurrentUserSid()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return identity.User?.Value ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Get motherboard UUID
        /// </summary>
        private static string GetMotherboardUuid()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT UUID FROM Win32_ComputerSystemProduct");
                using var results = searcher.Get();
                
                foreach (System.Management.ManagementObject obj in results)
                {
                    var uuid = obj["UUID"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(uuid) && 
                        !uuid.Equals("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase) &&
                        !uuid.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
                    {
                        return uuid;
                    }
                }
            }
            catch
            {
                // Ignore error
            }

            return "";
        }
    }
}
