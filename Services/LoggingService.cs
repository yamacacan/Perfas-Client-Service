using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Yapılandırılmış loglama servisi
    /// Sistem ve aktivite loglarını JSON formatında dosyalara yazar
    /// </summary>
    public class LoggingService
    {
        // Servis bağımlılıkları
        private readonly ILogger<LoggingService> _logger;
        private readonly string _logsDir; // Log dosyalarının kaydedileceği dizin

        /// <summary>
        /// Constructor - servis bağımlılıklarını alır
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="logsDir">Log dosyalarının dizini</param>
        public LoggingService(ILogger<LoggingService> logger, string logsDir)
        {
            _logger = logger;
            _logsDir = logsDir;
            
            // Log dizinini oluştur
            Directory.CreateDirectory(_logsDir);
        }

        /// <summary>
        /// Sistem log girişi yaz
        /// </summary>
        /// <param name="data">Yazılacak veri</param>
        /// <param name="ct">İptal token'ı</param>
        public async Task WriteSystemLogAsync(object data, CancellationToken ct)
        {
            try
            {
                // JSON formatına çevir ve yeni satır ekle
                var json = JsonSerializer.Serialize(data) + Environment.NewLine;
                
                // Günlük log dosyası oluştur (agent-2025-01-06.log)
                var logFile = Path.Combine(_logsDir, $"agent-{DateTimeOffset.Now:yyyy-MM-dd}.log");
                
                // Dosyaya ekle
                await File.AppendAllTextAsync(logFile, json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write system log");
                // Don't throw - logging failure shouldn't crash the application
            }
        }

        /// <summary>
        /// Aktivite log girişi yaz
        /// </summary>
        /// <param name="data">Yazılacak veri</param>
        /// <param name="ct">İptal token'ı</param>
        public async Task WriteActivityLogAsync(object data, CancellationToken ct)
        {
            try
            {
                // JSON formatına çevir ve yeni satır ekle
                var json = JsonSerializer.Serialize(data) + Environment.NewLine;
                
                // Günlük aktivite dosyası oluştur (activities-2025-01-06.jsonl)
                var dailyFile = Path.Combine(_logsDir, $"activities-{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
                
                // Dosyaya ekle
                await File.AppendAllTextAsync(dailyFile, json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write activity log");
                // Don't throw - logging failure shouldn't crash the application
            }
        }

        /// <summary>
        /// Info seviyesinde log yaz
        /// </summary>
        public async Task LogInfoAsync(string eventType, string message, object? data, Guid runId, CancellationToken ct)
        {
            try
            {
                var logEntry = new
                {
                    timestamp = DateTimeOffset.Now,
                    run_id = runId,
                    level = "info",
                    event_type = eventType,
                    message = message,
                    data = data
                };

                await WriteSystemLogAsync(logEntry, ct);
                _logger.LogInformation("[{EventType}] {Message}", eventType, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log info");
            }
        }

        /// <summary>
        /// Error seviyesinde log yaz
        /// </summary>
        public async Task LogErrorAsync(string eventType, string message, Exception exception, Guid runId, CancellationToken ct)
        {
            try
            {
                var logEntry = new
                {
                    timestamp = DateTimeOffset.Now,
                    run_id = runId,
                    level = "error",
                    event_type = eventType,
                    message = message,
                    exception = new
                    {
                        type = exception.GetType().Name,
                        message = exception.Message,
                        stackTrace = exception.StackTrace
                    }
                };

                await WriteSystemLogAsync(logEntry, ct);
                _logger.LogError(exception, "[{EventType}] {Message}", eventType, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log error");
            }
        }
    }
}
