using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Tek bir kayıt türü için JSONL dosya deposu. Dört veri türünün ortak yazma/okuma/silme
    /// mantığı burada tek yerde tutulur (DRY, tek sorumluluk).
    /// </summary>
    public sealed class JsonlFileStore<T> where T : class, ISourceTracked
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly ILogger _logger;
        private readonly string _directory;
        private readonly string _filePrefix;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // Okunduğu anki satır sayısı: okuma ile silme arasında eklenen satırlar kaybolmasın.
        private readonly ConcurrentDictionary<string, int> _readCounts = new(StringComparer.OrdinalIgnoreCase);

        public JsonlFileStore(ILogger logger, string directory, string filePrefix)
        {
            _logger = logger;
            _directory = Path.GetFullPath(directory);
            _filePrefix = filePrefix;
            Directory.CreateDirectory(_directory);
        }

        public async Task AppendAsync(T item, string fileName)
        {
            await _lock.WaitAsync();
            try
            {
                var path = Path.Combine(_directory, fileName);
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine);
            }
            finally { _lock.Release(); }
        }

        public async Task WriteAllAsync(IEnumerable<T> items, string fileName)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
                sb.AppendLine(JsonSerializer.Serialize(item, JsonOptions));

            await _lock.WaitAsync();
            try { await File.WriteAllTextAsync(Path.Combine(_directory, fileName), sb.ToString()); }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Senkronize edilmemiş kayıtları döndürür. Limit dosya bazında uygulanır
        /// (bir dosya ya tamamen okunur ya hiç okunmaz).
        /// </summary>
        public async Task<List<T>> GetUnsyncedAsync(int limit)
        {
            var result = new List<T>();
            await _lock.WaitAsync();
            try
            {
                var files = Directory.GetFiles(_directory, $"{_filePrefix}_*.jsonl")
                    .Where(f => !f.EndsWith(".synced.jsonl", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal);

                foreach (var file in files)
                {
                    if (result.Count >= limit) break;

                    var lines = await File.ReadAllLinesAsync(file);
                    _readCounts[file] = lines.Length;

                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
                            if (item == null) continue;
                            item.SourceFile = file;
                            result.Add(item);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Invalid JSON line in {File}", Path.GetFileName(file));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading unsynced {Prefix} data", _filePrefix);
            }
            finally { _lock.Release(); }

            return result;
        }

        /// <summary>
        /// Gönderilen kayıtları siler. Okumadan sonra eklenen satırlar korunur;
        /// yalnızca bu deponun dizinindeki dosyalara dokunulur.
        /// </summary>
        public async Task MarkSyncedAsync(IEnumerable<string> sourceFiles)
        {
            await _lock.WaitAsync();
            try
            {
                foreach (var file in sourceFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsInsideDirectory(file) || !File.Exists(file)) continue;

                    try
                    {
                        var lines = await File.ReadAllLinesAsync(file);
                        _readCounts.TryRemove(file, out var sent);

                        if (sent <= 0 || lines.Length <= sent)
                        {
                            File.Delete(file);
                        }
                        else
                        {
                            var tmp = file + ".tmp";
                            await File.WriteAllLinesAsync(tmp, lines.Skip(sent));
                            File.Move(tmp, file, overwrite: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Could not clean synced file {File}", Path.GetFileName(file));
                    }
                }
            }
            finally { _lock.Release(); }
        }

        // Path traversal koruması.
        private bool IsInsideDirectory(string path)
        {
            var root = _directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
