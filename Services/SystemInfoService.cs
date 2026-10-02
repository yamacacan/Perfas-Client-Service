using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WorkerService1.Services
{
    /// <summary>
    /// Sistem bilgisi toplama servisi - kullanıcı ve makine bilgilerini yakalar ve diske kaydeder
    /// Hostname, OS, donanım bilgileri, ağ bilgileri ve sistem özelliklerini toplar
    /// </summary>
    public class SystemInfoService
    {
        // Servis bağımlılıkları
        private readonly ILogger<SystemInfoService> _logger;
        private readonly string _systemDir; // Sistem bilgilerinin kaydedileceği dizin

        /// <summary>
        /// Constructor - servis bağımlılıklarını alır
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="systemDir">Sistem bilgilerinin dizini</param>
        public SystemInfoService(ILogger<SystemInfoService> logger, string systemDir)
        {
            _logger = logger;
            _systemDir = systemDir;
        }

        /// <summary>
        /// Sistem bilgilerini topla ve kaydet
        /// </summary>
        /// <param name="runId">Çalışma oturumu ID'si</param>
        /// <param name="ct">İptal token'ı</param>
        public async Task CollectSystemInfoAsync(Guid runId, CancellationToken ct)
        {
            try
            {
                // Sistem dizinini oluştur
                Directory.CreateDirectory(_systemDir);

                // Sistem bilgilerini topla
                var hostName = ResolveHostName();
                var osVersion = BuildOperatingSystemVersion();
                var hardware = GetHardwareInfo(hostName);
                var diskInfo = GetDiskInfo();
                var ramInfo = GetRamInfo();

                // Makine bilgilerini birleştir
                var machineInfo = new
                {
                    os_version = osVersion,
                    model = hardware.Model ?? "Unknown",
                    serial_number = hardware.SerialNumber ?? "Unknown",
                    disk_total_gb = diskInfo.TotalGB,
                    disk_used_gb = diskInfo.UsedGB,
                    disk_free_gb = diskInfo.FreeGB,
                    disk_usage_percent = diskInfo.UsagePercent,
                    ram_total_gb = ramInfo.TotalGB,
                    ram_used_gb = ramInfo.UsedGB,
                    ram_free_gb = ramInfo.FreeGB,
                    ram_usage_percent = ramInfo.UsagePercent
                };

                // JSON dosyasına kaydet
                var outputPath = Path.Combine(_systemDir, "machine-info.json");
                var json = JsonSerializer.Serialize(machineInfo, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outputPath, json, ct).ConfigureAwait(false);

                _logger.LogInformation("Machine information saved to {Path}", outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect machine information");
                throw;
            }
        }

        /// <summary>
        /// Hostname'i çözümle
        /// </summary>
        /// <returns>Makine adı</returns>
        private string ResolveHostName()
        {
            try
            {
                // DNS hostname'ini al
                var dns = Dns.GetHostName();
                if (!string.IsNullOrWhiteSpace(dns))
                {
                    return dns;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS host name lookup failed");
            }

            // Environment machine name'i kullan
            var machine = Environment.MachineName;
            return string.IsNullOrWhiteSpace(machine) ? "Unknown" : machine;
        }

        /// <summary>
        /// İşletim sistemi sürümünü oluştur
        /// </summary>
        /// <returns>OS sürüm bilgisi</returns>
        private string BuildOperatingSystemVersion()
        {
            // Yöntem 1: WMI'dan OS bilgisini al (en güvenilir kaynak)
            var wmiLabel = TryBuildOsLabelFromWmi();
            if (!string.IsNullOrWhiteSpace(wmiLabel))
            {
                _logger.LogDebug("OS version retrieved from WMI: {Label}", wmiLabel);
                return wmiLabel!;
            }

            // Yöntem 2: Registry'den OS bilgisini almaya çalış (yedek)
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var label = TryBuildOsLabelFromRegistry(view);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    _logger.LogDebug("OS version retrieved from Registry ({View}): {Label}", view, label);
                    return label!;
                }
            }

            // Yöntem 3: Environment'tan al (son çare)
            var envVersion = Environment.OSVersion.VersionString;
            _logger.LogDebug("OS version retrieved from Environment (fallback): {Label}", envVersion);
            return envVersion;
        }

        /// <summary>
        /// WMI'dan OS bilgisini almaya çalış
        /// </summary>
        private string? TryBuildOsLabelFromWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    var caption = NormalizeValue(obj["Caption"]);     // "Microsoft Windows 11 Pro"
                    var version = NormalizeValue(obj["Version"]);     // "10.0.22631"
                    var buildNumber = NormalizeValue(obj["BuildNumber"]); // "22631"

                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        // Caption zaten tam ismi içeriyor, build numarasını da ekleyelim
                        if (!string.IsNullOrWhiteSpace(buildNumber) && !caption.Contains(buildNumber))
                        {
                            return $"{caption} (build {buildNumber})";
                        }
                        return caption;
                    }

                    // Caption yoksa version kullan
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return $"Windows {version}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WMI query for OS version failed");
            }

            return null;
        }

        private string? TryBuildOsLabelFromRegistry(RegistryView view)
        {
            const string keyPath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(keyPath);
                if (key is null)
                {
                    _logger.LogDebug("Registry key not found for view {View}", view);
                    return null;
                }

                var productName = GetRegistryValueAsString(key, "ProductName");
                var displayVersion = GetRegistryValueAsString(key, "DisplayVersion");
                var releaseId = GetRegistryValueAsString(key, "ReleaseId");
                var currentBuild = GetRegistryValueAsString(key, "CurrentBuild");
                var ubr = GetRegistryValueAsString(key, "UBR");

                _logger.LogDebug("Registry values - ProductName: {ProductName}, DisplayVersion: {DisplayVersion}, CurrentBuild: {CurrentBuild}, UBR: {UBR}",
                    productName, displayVersion, currentBuild, ubr);

                // Windows 11 düzeltmesi: Build 22000 ve üzeri Windows 11'dir
                // Registry'de hâlâ "Windows 10" yazabilir, bunu düzeltiyoruz
                if (!string.IsNullOrWhiteSpace(productName) &&
                    !string.IsNullOrWhiteSpace(currentBuild) &&
                    int.TryParse(currentBuild, out var buildNumber) &&
                    buildNumber >= 22000 &&
                    productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                {
                    productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                    _logger.LogDebug("Corrected Windows 11 ProductName based on build number {Build}: {ProductName}", buildNumber, productName);
                }

                var buildSegment = string.IsNullOrWhiteSpace(currentBuild)
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(ubr) ? currentBuild : $"{currentBuild}.{ubr}";

                var releaseSegment = !string.IsNullOrWhiteSpace(displayVersion) ? displayVersion : releaseId;

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(productName))
                {
                    parts.Add(productName);
                }
                if (!string.IsNullOrWhiteSpace(releaseSegment))
                {
                    parts.Add(releaseSegment);
                }

                if (parts.Count == 0 && string.IsNullOrWhiteSpace(buildSegment))
                {
                    _logger.LogDebug("No valid OS info found in Registry for view {View}", view);
                    return null;
                }

                var descriptor = string.Join(" ", parts).Trim();
                if (!string.IsNullOrWhiteSpace(buildSegment))
                {
                    descriptor = string.IsNullOrWhiteSpace(descriptor)
                        ? $"(build {buildSegment})"
                        : $"{descriptor} (build {buildSegment})";
                }

                return descriptor;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Registry read failed for view {View}", view);
                return null;
            }
        }

        private (string? Manufacturer, string? Model, string? SerialNumber) GetHardwareInfo(string machineName)
        {
            var manufacturerCandidates = new List<string?>();
            var modelCandidates = new List<string?>();
            var serialCandidates = new List<string?>();

            void TryQuery(string query, Action<ManagementObject> handler)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\CIMV2", query);
                    using var results = searcher.Get();
                    foreach (ManagementObject obj in results)
                    {
                        handler(obj);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WMI query failed: {Query}", query);
                }
            }

            TryQuery("SELECT Manufacturer, Model FROM Win32_ComputerSystem", obj =>
            {
                manufacturerCandidates.Add(NormalizeValue(obj["Manufacturer"]));
                modelCandidates.Add(NormalizeValue(obj["Model"]));
            });

            TryQuery("SELECT Vendor, Name, IdentifyingNumber FROM Win32_ComputerSystemProduct", obj =>
            {
                manufacturerCandidates.Add(NormalizeValue(obj["Vendor"]));
                modelCandidates.Add(NormalizeValue(obj["Name"]));
                serialCandidates.Add(NormalizeValue(obj["IdentifyingNumber"]));
            });

            TryQuery("SELECT Manufacturer, Product FROM Win32_BaseBoard", obj =>
            {
                manufacturerCandidates.Add(NormalizeValue(obj["Manufacturer"]));
                modelCandidates.Add(NormalizeValue(obj["Product"]));
            });

            TryQuery("SELECT SerialNumber FROM Win32_BIOS", obj =>
            {
                serialCandidates.Add(NormalizeValue(obj["SerialNumber"]));
            });

            var manufacturer = HardwareValueSanitizer.SelectHardwareCandidate(manufacturerCandidates, machineName);
            var model = HardwareValueSanitizer.SelectHardwareCandidate(modelCandidates, machineName);
            var serial = HardwareValueSanitizer.SelectSerialCandidate(serialCandidates, machineName);

            return (manufacturer, model, serial);
        }

        private static string GetRegistryValueAsString(RegistryKey key, string valueName)
        {
            try
            {
                var value = key.GetValue(valueName);
                return value switch
                {
                    null => string.Empty,
                    string s => s.Trim(),
                    int i => i.ToString(),
                    long l => l.ToString(),
                    _ => value.ToString() ?? string.Empty
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string? NormalizeValue(object? value)
        {
            var str = value?.ToString();
            return string.IsNullOrWhiteSpace(str) ? null : str.Trim();
        }

        /// <summary>
        /// Disk bilgilerini topla (tüm diskler toplam)
        /// </summary>
        /// <returns>Disk kullanım bilgileri</returns>
        private (double TotalGB, double UsedGB, double FreeGB, double UsagePercent) GetDiskInfo()
        {
            try
            {
                double totalBytes = 0;
                double freeBytes = 0;

                // Tüm sabit diskleri topla
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);

                foreach (var drive in drives)
                {
                    totalBytes += drive.TotalSize;
                    freeBytes += drive.AvailableFreeSpace;
                }

                double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                double freeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 2);
                double usedGB = Math.Round(totalGB - freeGB, 2);
                double usagePercent = totalGB > 0 ? Math.Round((usedGB / totalGB) * 100, 2) : 0;

                return (totalGB, usedGB, freeGB, usagePercent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get disk information");
                return (0, 0, 0, 0);
            }
        }

        /// <summary>
        /// RAM bilgilerini topla
        /// </summary>
        /// <returns>RAM kullanım bilgileri</returns>
        private (double TotalGB, double UsedGB, double FreeGB, double UsagePercent) GetRamInfo()
        {
            try
            {
                double totalBytes = 0;
                double freeBytes = 0;

                // WMI ile RAM bilgilerini al
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    // WMI değerleri KB cinsinden geliyor
                    var totalKB = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                    var freeKB = Convert.ToDouble(obj["FreePhysicalMemory"]);

                    totalBytes = totalKB * 1024;
                    freeBytes = freeKB * 1024;
                    break; // İlk sonuç yeterli
                }

                double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                double freeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 2);
                double usedGB = Math.Round(totalGB - freeGB, 2);
                double usagePercent = totalGB > 0 ? Math.Round((usedGB / totalGB) * 100, 2) : 0;

                return (totalGB, usedGB, freeGB, usagePercent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get RAM information");
                return (0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Motherboard UUID'yi al
        /// </summary>
        private string? GetMotherboardUuid()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT UUID FROM Win32_ComputerSystemProduct");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    var uuid = NormalizeValue(obj["UUID"]);
                    if (!string.IsNullOrWhiteSpace(uuid) &&
                        !uuid.Equals("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase) &&
                        !uuid.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
                    {
                        return uuid;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get motherboard UUID");
            }

            return null;
        }

        /// <summary>
        /// Sistem bilgilerini toplama metodu - MySQL sync için
        /// </summary>
        public SystemInfoData GetSystemInfoData()
        {
            var hostName = ResolveHostName();
            var osVersion = BuildOperatingSystemVersion();
            var hardware = GetHardwareInfo(hostName);
            var motherboardUuid = GetMotherboardUuid();
            var diskInfo = GetDiskInfo();
            var ramInfo = GetRamInfo();

            return new SystemInfoData
            {
                HostName = hostName,
                OsVersion = osVersion,
                Model = hardware.Model,
                SerialNumber = hardware.SerialNumber,
                MotherboardUuid = motherboardUuid,
                DiskTotalGB = diskInfo.TotalGB,
                DiskUsedGB = diskInfo.UsedGB,
                DiskFreeGB = diskInfo.FreeGB,
                DiskUsagePercent = diskInfo.UsagePercent,
                RamTotalGB = ramInfo.TotalGB,
                RamUsedGB = ramInfo.UsedGB,
                RamFreeGB = ramInfo.FreeGB,
                RamUsagePercent = ramInfo.UsagePercent
            };
        }
    }

    /// <summary>
    /// Sistem bilgisi data model - MySQL sync için
    /// </summary>
    public class SystemInfoData
    {
        public string HostName { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string? SerialNumber { get; set; }
        public string? MotherboardUuid { get; set; }
        public double DiskTotalGB { get; set; }
        public double DiskUsedGB { get; set; }
        public double DiskFreeGB { get; set; }
        public double DiskUsagePercent { get; set; }
        public double RamTotalGB { get; set; }
        public double RamUsedGB { get; set; }
        public double RamFreeGB { get; set; }
        public double RamUsagePercent { get; set; }
    }
}
