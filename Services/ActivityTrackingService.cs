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
    /// Kullanıcının aktif pencere değişikliklerini takip eder ve JSONL dosyasına kaydeder
    /// </summary>
    public class ActivityTrackingService
    {
        // Servis bağımlılıkları
        private readonly ILogger<ActivityTrackingService> _logger;
        private readonly string _activitiesDir;
        private readonly bool _enableConsoleOutput;
        private readonly IActivityStore _storageService;

        // Aktivite durumu
        private int _currentPid = -1;
        private string _currentProcess = "";
        private string _currentTitle = "";
        private DateTimeOffset _sessionStart = DateTimeOffset.MinValue;

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
            IActivityStore storageService,
            bool enableConsoleOutput = true)
        {
            _logger = logger;
            _activitiesDir = activitiesDir;
            _storageService = storageService;
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
                // Aynı pencere - hiçbir şey yapma
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
        /// Log activity to JSONL storage
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
                
                _logger.LogDebug("Creating ActivityLog - UserSid: '{UserSid}', MotherboardUuid: '{Uuid}'", 
                    userSid, motherboardUuid);

                // JSONL storage'a kaydet
                var activity = new ActivityLog
                {
                    Evt = "activity",
                    ActivityType = "window",
                    ProcessName = process,
                    Title = title,
                    StartTimeUtc = _sessionStart.UtcDateTime,
                    EndTimeUtc = endTime.UtcDateTime,
                    DurationMs = durationMs,
                    Username = username,
                    Domain = domain,
                    UserSid = userSid,
                    MotherboardUuid = motherboardUuid,
                    CreatedAtUtc = DateTime.UtcNow
                };

                await _storageService.SaveActivityAsync(activity);
                
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
