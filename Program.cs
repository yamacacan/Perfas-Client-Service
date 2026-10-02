using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkerService1.Services;
using WorkerService1;
using System.IO;
using System.Linq;

// Explicitly initialize SQLitePCL
SQLitePCL.Batteries_V2.Init();

// Check if running as a service or agent
bool isService = args.Contains("--service");
bool isAgent = args.Contains("--agent") || !isService;

// Get the exe directory - important for finding appsettings.json
var exeDirectory = AppContext.BaseDirectory;

// Create builder with content root set to exe directory
// This ensures appsettings.json is found from Program Files when agent is auto-started
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = exeDirectory
});

// Also explicitly load appsettings from the exe directory
builder.Configuration.AddJsonFile(Path.Combine(exeDirectory, "appsettings.json"), optional: false, reloadOnChange: true);

// Enable Windows Service support
if (isService)
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "PerformanceAgentService";
    });
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Register essential services
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton(provider =>
{
    var configurationService = provider.GetRequiredService<ConfigurationService>();
    return configurationService.GetValidatedConfig();
});

// Register JSONL Storage Service (Replaces SQLite)
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<JsonlStorageService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    return new JsonlStorageService(logger, dataDirectory);
});
builder.Services.AddSingleton<ISyncStorage>(p => p.GetRequiredService<JsonlStorageService>());
builder.Services.AddSingleton<IActivityStore>(p => p.GetRequiredService<JsonlStorageService>());
builder.Services.AddSingleton<IBrowserStore>(p => p.GetRequiredService<JsonlStorageService>());
builder.Services.AddSingleton<IInstalledAppStore>(p => p.GetRequiredService<JsonlStorageService>());
builder.Services.AddSingleton<IHardwareStore>(p => p.GetRequiredService<JsonlStorageService>());

builder.Services.AddSingleton<IBrowserHistoryReader, BrowserHistoryReader>();

// BrowserHistoryService (Mainly for Agent)
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<BrowserHistoryService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var storageService = provider.GetRequiredService<JsonlStorageService>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var activitiesDir = Path.Combine(dataDirectory, "data", "activities");
    var username = Environment.UserName ?? "Unknown";
    var domain = Environment.UserDomainName ?? "Unknown";
    var reader = provider.GetRequiredService<IBrowserHistoryReader>();
    return new BrowserHistoryService(logger, dataDirectory, storageService, reader, activitiesDir, username, domain, new BrowserHistoryMonitoringOptions());
});

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<SystemInfoService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var systemDir = Path.Combine(dataDirectory, "data", "system");
    return new SystemInfoService(logger, systemDir);
});

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<UserInfoService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var systemDir = Path.Combine(dataDirectory, "data", "system");
    return new UserInfoService(logger, systemDir);
});

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<SystemStateService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var activitiesDir = Path.Combine(dataDirectory, "data", "activities");
    var username = Environment.UserName ?? "Unknown";
    var domain = Environment.UserDomainName ?? "Unknown";
    return new SystemStateService(logger, activitiesDir, username, domain, config.EnableConsoleOutput);
});

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ApplicationCollectionService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var systemDir = Path.Combine(dataDirectory, "data", "system");
    return new ApplicationCollectionService(logger, systemDir, config.EnableUwpCollection, config.EnableWin32Collection);
});

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ActivityTrackingService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var storageService = provider.GetRequiredService<JsonlStorageService>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var activitiesDir = Path.Combine(dataDirectory, "data", "activities");
    return new ActivityTrackingService(logger, activitiesDir, storageService, config.EnableConsoleOutput);
});

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<LoggingService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var logsDir = Path.Combine(dataDirectory, "logs");
    return new LoggingService(logger, logsDir);
});

builder.Services.AddSingleton(provider =>
    DataSyncOptionsLoader.Load(
        provider.GetRequiredService<IConfiguration>(),
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("DataSyncOptions")));

// Ayarlar eksik/geçersizse uygulama çökmesin: senkronizasyon devre dışı istemciyle çalışır.
builder.Services.AddSingleton<IIngestClient>(provider =>
{
    var options = provider.GetRequiredService<DataSyncOptions>();
    var logger = provider.GetRequiredService<ILoggerFactory>();
    if (!options.IsConfigured)
        return new DisabledIngestClient();
    try
    {
        return new HttpIngestClient(
            logger.CreateLogger<HttpIngestClient>(),
            new AesEncryptionService(options.AesKey),
            options);
    }
    catch (ArgumentException ex)
    {
        logger.CreateLogger("DataSync").LogError(ex, "DataSync yapılandırması geçersiz; senkronizasyon devre dışı.");
        return new DisabledIngestClient();
    }
});
builder.Services.AddSingleton<DataSyncService>();

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HealthCheckService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var browserHistoryService = provider.GetRequiredService<BrowserHistoryService>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    return new HealthCheckService(logger, browserHistoryService, dataDirectory);
});

builder.Services.AddSingleton<IActiveWindowProvider, WinApiActiveWindowProvider>();

// Periodic inventory tasks (new tasks only need to be registered here)
builder.Services.AddSingleton<IPeriodicTask, HardwareInventoryTask>();
builder.Services.AddSingleton<IPeriodicTask, AppInventoryTask>();

builder.Services.AddHostedService(provider => new Worker(
    provider.GetRequiredService<ILogger<Worker>>(),
    provider.GetRequiredService<PerformanceAgentConfig>(),
    provider.GetRequiredService<ActivityTrackingService>(),
    provider.GetRequiredService<BrowserHistoryService>(),
    provider.GetRequiredService<IActiveWindowProvider>(),
    provider.GetServices<IPeriodicTask>(),
    isService));

var app = builder.Build();

// Senkronizasyon yalnızca SYSTEM servisinde çalışır (API sırları sadece SYSTEM/Administrators tarafından okunabilir).
// Kullanıcı bağlamındaki ajan sır okumaz, yalnızca veri toplar.
if (isService)
{
    app.Services.GetRequiredService<DataSyncService>();
}

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var mainLogger = loggerFactory.CreateLogger("PerformanceAgent");

mainLogger.LogInformation("Performance Agent starting (Mode: {Mode})...", isService ? "Service" : "Agent");

// Validation
var configServiceValidate = app.Services.GetRequiredService<ConfigurationService>();
var configValidate = app.Services.GetRequiredService<PerformanceAgentConfig>();

try
{
    var dataDirectory = configServiceValidate.GetSafeDataDirectory(configValidate.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "activities"));
    Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "browser"));
    Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "system"));
    Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
    
    // Fix permissions for service usage
    var configService = app.Services.GetRequiredService<ConfigurationService>();
    // Call permissions helper if it's public, otherwise assume directory creation is enough or handled by external script
    // configService.EnsureDirectoryWithPermissions(dataDirectory); 
    
    mainLogger.LogInformation("✅ System validation completed. Data directory: {Path}", dataDirectory);
}
catch (Exception ex)
{
    mainLogger.LogError(ex, "❌ Validation failed");
    if (!isService)
    {
        Console.WriteLine($"Error: {ex.Message}");
        Task.Delay(5000).Wait();
    }
    return 1;
}

await app.RunAsync();
return 0;
