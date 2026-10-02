using System.Collections.Generic;
using System.Threading.Tasks;

namespace WorkerService1.Services
{
    // Arayüz ayrımı (ISP): her tüketici yalnızca ihtiyaç duyduğu depoya bağlanır.

    public interface IActivityStore
    {
        Task SaveActivityAsync(ActivityLog activity);
        Task<List<ActivityLog>> GetUnsyncedActivitiesAsync(int limit = 100);
        Task MarkActivitiesAsSyncedAsync(List<string> sourceFiles);
    }

    public interface IBrowserStore
    {
        Task SaveBrowserDataAsync(BrowserData data);
        Task<List<BrowserData>> GetUnsyncedBrowserDataAsync(int limit = 100);
        Task MarkBrowserDataAsSyncedAsync(List<string> sourceFiles);
    }

    public interface IInstalledAppStore
    {
        Task SaveInstalledAppsAsync(List<InstalledAppData> apps);
        Task<List<InstalledAppData>> GetUnsyncedInstalledAppsAsync(int limit = 500);
        Task MarkInstalledAppsAsSyncedAsync(List<string> sourceFiles);
    }

    public interface IHardwareStore
    {
        Task SaveSystemHardwareAsync(SystemHardwareData hardware);
        Task<List<SystemHardwareData>> GetUnsyncedSystemHardwareAsync(int limit = 100);
        Task MarkSystemHardwareAsSyncedAsync(List<string> sourceFiles);
    }

    /// <summary>Senkronizasyon servisinin ihtiyaç duyduğu tüm depolar.</summary>
    public interface ISyncStorage : IActivityStore, IBrowserStore, IInstalledAppStore, IHardwareStore { }
}
