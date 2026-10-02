using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>Tarayıcı geçmiş veritabanlarından son ziyaretleri okur.</summary>
    public interface IBrowserHistoryReader
    {
        Task<List<BrowserHistoryEntry>> GetChromeRecentUrlsAsync(string historyPath, DateTime since, string browserName);
        Task<List<BrowserHistoryEntry>> GetFirefoxRecentUrlsAsync(string historyPath, DateTime since, string browserName);
    }

    /// <summary>
    /// Chromium/Firefox geçmiş DB'lerini (kilitli olduğu için geçici kopya üzerinden, salt okunur) okur.
    /// İzleme/olay mantığından bağımsızdır (tek sorumluluk).
    /// </summary>
    public class BrowserHistoryReader : IBrowserHistoryReader
    {
        // Chrome epoch - Chrome'un zaman damgası başlangıcı (1601-01-01)
        private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ILogger<BrowserHistoryReader> _logger;

        public BrowserHistoryReader(ILogger<BrowserHistoryReader> logger)
        {
            _logger = logger;
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

                using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly;Pooling=False");
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
                         AND (v.originator_cache_guid IS NULL OR v.originator_cache_guid = '')
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

                using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly;Pooling=False");
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
                       AND (v.source = 3 OR v.from_visit != 0)
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
    }
}
