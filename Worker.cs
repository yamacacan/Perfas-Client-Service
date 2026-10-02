using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkerService1.Services;

namespace WorkerService1
{
    /// <summary>
    /// Ana döngü. Yalnızca zamanlama yapar; toplama işleri <see cref="IPeriodicTask"/> olarak enjekte edilir.
    /// </summary>
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly TimeSpan _pollInterval;
        private readonly bool _isService;

        private readonly ActivityTrackingService _activityTracking;
        private readonly BrowserHistoryService _browserHistory;
        private readonly IActiveWindowProvider _windowProvider;
        private readonly IReadOnlyList<IPeriodicTask> _tasks;

        private readonly string _username = Environment.UserName ?? "Unknown";
        private readonly string _domain = Environment.UserDomainName ?? "Unknown";

        public Worker(
            ILogger<Worker> logger,
            PerformanceAgentConfig config,
            ActivityTrackingService activityTracking,
            BrowserHistoryService browserHistory,
            IActiveWindowProvider windowProvider,
            IEnumerable<IPeriodicTask> tasks,
            bool isService)
        {
            _logger = logger;
            _activityTracking = activityTracking;
            _browserHistory = browserHistory;
            _windowProvider = windowProvider;
            _tasks = tasks.ToList();
            _isService = isService;
            _pollInterval = TimeSpan.FromMilliseconds(config.PollIntervalMs);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker started as {Mode} at {Time}", _isService ? "Service" : "Agent", DateTimeOffset.Now);

            if (!_isService)
                await _browserHistory.StartRealTimeMonitoringAsync();

            var lastRun = _tasks.ToDictionary(t => t, _ => DateTime.MinValue);
            var delay = _isService ? TimeSpan.FromMinutes(1) : _pollInterval;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_isService)
                        await TrackActiveWindowAsync(stoppingToken);

                    // Envanter işleri kullanıcı bağlamı ister; SYSTEM servisinde çalıştırılmaz.
                    foreach (var task in _isService ? Array.Empty<IPeriodicTask>() : _tasks)
                    {
                        if (DateTime.Now - lastRun[task] < task.Interval) continue;
                        await RunTaskAsync(task, lastRun, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in worker loop");
                }

                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task TrackActiveWindowAsync(CancellationToken ct)
        {
            var (pid, procName, title) = _windowProvider.GetActiveWindowInfo();
            if (!string.IsNullOrEmpty(procName))
                await _activityTracking.ProcessWindowChangeAsync(pid, procName, title ?? "", _username, _domain, ct);
        }

        private async Task RunTaskAsync(IPeriodicTask task, Dictionary<IPeriodicTask, DateTime> lastRun, CancellationToken ct)
        {
            try
            {
                await task.RunAsync(ct);
                lastRun[task] = DateTime.Now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Task} failed", task.Name);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Worker stopping...");
            if (!_isService)
                await _activityTracking.LogCurrentActivityAsync(_username, _domain, cancellationToken);
            await base.StopAsync(cancellationToken);
        }
    }
}
