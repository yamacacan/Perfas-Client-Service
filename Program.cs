using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkerService1.Services;
using WorkerService1;

// Ana uygulama başlatma dosyası
// Bu dosya Dependency Injection container'ını kurar ve servisleri kaydeder
var builder = Host.CreateApplicationBuilder(args);

// Logging konfigürasyonu
// Konsol çıktısını temizler ve sadece Information seviyesinde logları gösterir
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Temel servisleri kaydet
// ConfigurationService - Konfigürasyon dosyalarını okur ve doğrular
builder.Services.AddSingleton<ConfigurationService>();

// Validated configuration'ı singleton olarak kaydet
// Bu sayede tüm servisler aynı konfigürasyonu kullanır
builder.Services.AddSingleton(provider =>
{
    var configurationService = provider.GetRequiredService<ConfigurationService>();
    return configurationService.GetValidatedConfig();
});

// ActivityDatabaseService'i kaydet - SQLite veritabanı servisi
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ActivityDatabaseService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    return new ActivityDatabaseService(logger, dataDirectory);
});

// BrowserHistoryService'i kaydet (Real-time Monitoring)
// Chrome, Edge, Firefox tarayıcı geçmişlerini real-time izler ve activity log'ları günceller
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<BrowserHistoryService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var databaseService = provider.GetRequiredService<ActivityDatabaseService>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var activitiesDir = Path.Combine(dataDirectory, "data", "activities");
    var username = Environment.UserName ?? "Unknown";
    var domain = Environment.UserDomainName ?? "Unknown";
    var monitoringOptions = new BrowserHistoryMonitoringOptions();
    return new BrowserHistoryService(logger, dataDirectory, databaseService, activitiesDir, username, domain, monitoringOptions);
});

// SystemInfoService'i kaydet
// Sistem bilgilerini toplar (hostname, OS, donanım vb.)
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<SystemInfoService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var systemDir = Path.Combine(dataDirectory, "data", "system");
    return new SystemInfoService(logger, systemDir);
});

// UserInfoService'i kaydet
// Kullanıcı bilgilerini toplar (username, domain, profil yolları vb.)
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<UserInfoService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var systemDir = Path.Combine(dataDirectory, "data", "system");
    return new UserInfoService(logger, systemDir);
});

// SystemStateService'i kaydet
// Sistem durumu olaylarını izler (kilit, ekran koruyucu, uyku modu, kapanma vb.)
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

// ApplicationCollectionService'i kaydet
// Yüklü uygulamaları toplar (UWP ve Win32)
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ApplicationCollectionService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var systemDir = Path.Combine(dataDirectory, "data", "system");
    return new ApplicationCollectionService(logger, systemDir, config.EnableUwpCollection, config.EnableWin32Collection);
});

// ActivityTrackingService'i kaydet (URL'siz Log Yazma)
// Kullanıcı aktivite oturumlarını yönetir ve URL'siz log yazar
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ActivityTrackingService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var databaseService = provider.GetRequiredService<ActivityDatabaseService>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var activitiesDir = Path.Combine(dataDirectory, "data", "activities");
    return new ActivityTrackingService(logger, activitiesDir, databaseService, config.EnableConsoleOutput);
});

// LoggingService'i kaydet
// Yapılandırılmış loglama servisi
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<LoggingService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    var logsDir = Path.Combine(dataDirectory, "logs");
    return new LoggingService(logger, logsDir);
});

// DataSyncService'i kaydet
// SQLite'dan uzak sunucuya veri senkronizasyonu
builder.Services.AddSingleton<DataSyncService>();

// HealthCheckService'i kaydet
// Sistem sağlık durumu kontrol servisi
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HealthCheckService>>();
    var configService = provider.GetRequiredService<ConfigurationService>();
    var config = provider.GetRequiredService<PerformanceAgentConfig>();
    var databaseService = provider.GetRequiredService<ActivityDatabaseService>();
    var browserHistoryService = provider.GetRequiredService<BrowserHistoryService>();
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    return new HealthCheckService(logger, databaseService, browserHistoryService, dataDirectory);
});

// IActiveWindowProvider'ı kaydet
// Aktif pencere bilgilerini almak için Windows API implementasyonu
builder.Services.AddSingleton<IActiveWindowProvider, WinApiActiveWindowProvider>();

// Worker'ı hosted service olarak kaydet
// Ana arka plan servisi - tüm veri toplama işlemlerini koordine eder
builder.Services.AddHostedService<Worker>();

// Uygulamayı oluştur ve başlat
var app = builder.Build();

// Başlangıç bilgilerini göster
var config = app.Services.GetRequiredService<PerformanceAgentConfig>();
var configService = app.Services.GetRequiredService<ConfigurationService>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PerformanceAgent");

logger.LogInformation("Performance Agent starting...");

// Startup validation - Sistem gereksinimlerini kontrol et
try
{
    var dataDirectory = configService.GetSafeDataDirectory(config.DataDirectory);
    logger.LogInformation("Data directory: {Path}", dataDirectory);
    
    // Gerekli klasörleri oluştur
    Directory.CreateDirectory(dataDirectory);
    Directory.CreateDirectory(Path.Combine(dataDirectory, "data"));
    Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "activities"));
    Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "system"));
    Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
    
    // Yazma iznini test et
    var testFile = Path.Combine(dataDirectory, ".write-test");
    File.WriteAllText(testFile, "test");
    File.Delete(testFile);
    
    logger.LogInformation("✅ System validation completed successfully");
    logger.LogInformation("   - OS: {OS}", Environment.OSVersion);
    logger.LogInformation("   - .NET: {Runtime}", Environment.Version);
    logger.LogInformation("   - User: {User}", Environment.UserName);
    logger.LogInformation("   - Machine: {Machine}", Environment.MachineName);
}
catch (Exception ex)
{
    logger.LogError(ex, "❌ System validation failed - cannot start application");
    Console.WriteLine($"\nERROR: System validation failed!");
    Console.WriteLine($"Reason: {ex.Message}");
    Console.WriteLine($"\nPlease ensure:");
    Console.WriteLine($"  1. Application has write permissions");
    Console.WriteLine($"  2. Disk space is available");
    Console.WriteLine($"  3. Data directory is accessible: {config.DataDirectory}");
    Console.WriteLine($"\nPress any key to exit...");
    Console.ReadKey();
    return 1;
}

try
{
    // Uygulamayı çalıştır
    await app.RunAsync();
}
catch (Exception ex)
{
    // Hata durumunda logla ve çık
    logger.LogError(ex, "Performance Agent failed during execution");
    Console.WriteLine($"\nError: {ex.Message}");
    return 1;
}

// Başarılı kapanış
logger.LogInformation("Performance Agent stopped");
return 0;
