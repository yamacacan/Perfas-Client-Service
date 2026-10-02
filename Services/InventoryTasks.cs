using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WorkerService1.Services
{
    /// <summary>
    /// Periyodik çalışan bir iş. Worker yeni iş türleri için değiştirilmeden genişletilir (OCP).
    /// </summary>
    public interface IPeriodicTask
    {
        string Name { get; }
        TimeSpan Interval { get; }
        Task RunAsync(CancellationToken ct);
    }

    /// <summary>Donanım/sistem bilgisini toplayıp depolar.</summary>
    public sealed class HardwareInventoryTask : IPeriodicTask
    {
        private readonly ILogger<HardwareInventoryTask> _logger;
        private readonly SystemInfoService _systemInfo;
        private readonly IHardwareStore _store;

        public HardwareInventoryTask(ILogger<HardwareInventoryTask> logger, SystemInfoService systemInfo, IHardwareStore store)
        {
            _logger = logger;
            _systemInfo = systemInfo;
            _store = store;
        }

        public string Name => "Hardware inventory";
        public TimeSpan Interval => TimeSpan.FromMinutes(10);

        public async Task RunAsync(CancellationToken ct)
        {
            var info = _systemInfo.GetSystemInfoData();

            await _store.SaveSystemHardwareAsync(new SystemHardwareData
            {
                Hostname = Environment.MachineName,
                Username = Environment.UserName ?? "Unknown",
                Domain = Environment.UserDomainName ?? "Unknown",
                UserSid = SystemIdentityHelper.GetCurrentUserSid(),
                OsVersion = info.OsVersion,
                Model = info.Model,
                SerialNumber = info.SerialNumber,
                MotherboardUuid = info.MotherboardUuid,
                DiskTotalGB = info.DiskTotalGB,
                DiskUsedGB = info.DiskUsedGB,
                DiskFreeGB = info.DiskFreeGB,
                DiskUsagePercent = info.DiskUsagePercent,
                RamTotalGB = info.RamTotalGB,
                RamUsedGB = info.RamUsedGB,
                RamFreeGB = info.RamFreeGB,
                RamUsagePercent = info.RamUsagePercent,
                CollectedAt = DateTime.UtcNow
            });
            _logger.LogInformation("Hardware info collected.");
        }
    }

    /// <summary>Yüklü uygulama envanterini toplayıp depolar.</summary>
    public sealed class AppInventoryTask : IPeriodicTask
    {
        private readonly ILogger<AppInventoryTask> _logger;
        private readonly ApplicationCollectionService _collector;
        private readonly IInstalledAppStore _store;
        private readonly Guid _runId = Guid.NewGuid();

        public AppInventoryTask(
            ILogger<AppInventoryTask> logger,
            ApplicationCollectionService collector,
            IInstalledAppStore store,
            PerformanceAgentConfig config)
        {
            _logger = logger;
            _collector = collector;
            _store = store;
            Interval = TimeSpan.FromHours(config.AppsRefreshIntervalHours);
        }

        public string Name => "App inventory";
        public TimeSpan Interval { get; }

        public async Task RunAsync(CancellationToken ct)
        {
            var apps = await _collector.CollectInstalledApplicationsAsync(_runId, ct);
            var userSid = SystemIdentityHelper.GetCurrentUserSid();
            var machine = Environment.MachineName;
            var uuid = SystemIdentityHelper.GetMotherboardUuid();
            var user = Environment.UserName ?? "Unknown";
            var domain = Environment.UserDomainName ?? "Unknown";

            await _store.SaveInstalledAppsAsync(apps.Select(name => new InstalledAppData
            {
                AppName = name,
                Username = user,
                Domain = domain,
                UserSid = userSid,
                MotherboardUuid = uuid,
                MachineName = machine,
                CollectedAt = DateTime.UtcNow
            }).ToList());
            _logger.LogInformation("App inventory collected.");
        }
    }
}
