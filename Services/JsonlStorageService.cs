using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// JSONL tabanlı depolama cephesi: dört kayıt türünü kendi <see cref="JsonlFileStore{T}"/> örneğine yönlendirir.
    /// </summary>
    public class JsonlStorageService : ISyncStorage
    {
        private readonly JsonlFileStore<ActivityLog> _activities;
        private readonly JsonlFileStore<BrowserData> _browser;
        private readonly JsonlFileStore<InstalledAppData> _apps;
        private readonly JsonlFileStore<SystemHardwareData> _hardware;

        public JsonlStorageService(ILogger<JsonlStorageService> logger, string dataDirectory)
        {
            var activitiesDir = Path.Combine(dataDirectory, "data", "activities");
            var browserDir = Path.Combine(dataDirectory, "data", "browser");
            var systemDir = Path.Combine(dataDirectory, "data", "system");

            _activities = new JsonlFileStore<ActivityLog>(logger, activitiesDir, "activities");
            _browser = new JsonlFileStore<BrowserData>(logger, browserDir, "browser");
            _apps = new JsonlFileStore<InstalledAppData>(logger, systemDir, "apps");
            _hardware = new JsonlFileStore<SystemHardwareData>(logger, systemDir, "hardware");
        }

        private static string User => Environment.UserName ?? "unknown";

        public Task SaveActivityAsync(ActivityLog activity) =>
            _activities.AppendAsync(activity, $"activities_{User}_{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        public Task<List<ActivityLog>> GetUnsyncedActivitiesAsync(int limit = 100) => _activities.GetUnsyncedAsync(limit);
        public Task MarkActivitiesAsSyncedAsync(List<string> sourceFiles) => _activities.MarkSyncedAsync(sourceFiles);

        public Task SaveBrowserDataAsync(BrowserData data) =>
            _browser.AppendAsync(data, $"browser_{User}_{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        public Task<List<BrowserData>> GetUnsyncedBrowserDataAsync(int limit = 100) => _browser.GetUnsyncedAsync(limit);
        public Task MarkBrowserDataAsSyncedAsync(List<string> sourceFiles) => _browser.MarkSyncedAsync(sourceFiles);

        public Task SaveInstalledAppsAsync(List<InstalledAppData> apps) =>
            _apps.WriteAllAsync(apps, $"apps_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.jsonl");
        public Task<List<InstalledAppData>> GetUnsyncedInstalledAppsAsync(int limit = 500) => _apps.GetUnsyncedAsync(limit);
        public Task MarkInstalledAppsAsSyncedAsync(List<string> sourceFiles) => _apps.MarkSyncedAsync(sourceFiles);

        public Task SaveSystemHardwareAsync(SystemHardwareData hardware) =>
            _hardware.AppendAsync(hardware, $"hardware_{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        public Task<List<SystemHardwareData>> GetUnsyncedSystemHardwareAsync(int limit = 100) => _hardware.GetUnsyncedAsync(limit);
        public Task MarkSystemHardwareAsSyncedAsync(List<string> sourceFiles) => _hardware.MarkSyncedAsync(sourceFiles);
    }
}
