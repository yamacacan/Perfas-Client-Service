using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Windows.ApplicationModel;
using System.Xml.Linq;

namespace WorkerService1.Services
{
    /// <summary>
    /// Uygulama toplama servisi - Win32 ve UWP uygulamaları için
    /// Yüklü uygulamaları toplar, kategorize eder ve JSON formatında kaydeder
    /// </summary>
    public class ApplicationCollectionService
    {
        // Servis bağımlılıkları
        private readonly ILogger<ApplicationCollectionService> _logger;
        private readonly string _systemDir; // Sistem bilgilerinin dizini
        private readonly bool _enableUwpCollection; // UWP uygulamalarını topla
        private readonly bool _enableWin32Collection; // Win32 uygulamalarını topla

        public ApplicationCollectionService(
            ILogger<ApplicationCollectionService> logger, 
            string systemDir,
            bool enableUwpCollection = true,
            bool enableWin32Collection = true)
        {
            _logger = logger;
            _systemDir = systemDir;
            _enableUwpCollection = enableUwpCollection;
            _enableWin32Collection = enableWin32Collection;
        }

        /// <summary>
        /// Collect installed applications
        /// </summary>
        public async Task<List<string>> CollectInstalledApplicationsAsync(Guid runId, CancellationToken ct)
        {
            try
            {
                var appsRaw = new List<string>();

                // Collect Win32 applications
                if (_enableWin32Collection)
                {
                    var win32Apps = await ReadWin32UninstallNamesAsync();
                    appsRaw.AddRange(win32Apps);
                    _logger.LogDebug("Collected {Count} Win32 applications", win32Apps.Count());
                }

                // Collect UWP applications
                if (_enableUwpCollection)
                {
                    var uwpApps = await ReadUwpApplicationsAsync();
                    appsRaw.AddRange(uwpApps);
                    _logger.LogDebug("Collected {Count} UWP applications", uwpApps.Count());
                }

                // Clean and deduplicate
                var appsClean = appsRaw
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(NormalizeAppName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Save to file
                await SaveApplicationsToFileAsync(appsClean, runId, ct);

                _logger.LogInformation("Collected {Count} unique applications", appsClean.Count);
                return appsClean;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect installed applications");
                throw;
            }
        }

        /// <summary>
        /// Read Win32 applications from registry
        /// </summary>
        private static async Task<IEnumerable<string>> ReadWin32UninstallNamesAsync()
        {
            return await Task.Run(() => ReadWin32UninstallNames());
        }

        private static IEnumerable<string> ReadWin32UninstallNames()
        {
            var list = new List<string>();

            // HKLM 64-bit
            using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                           .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (hklm64 != null)
                    list.AddRange(ReadUninstallKey(hklm64));
            }

            // HKLM 32-bit
            using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                           .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (hklm32 != null)
                    list.AddRange(ReadUninstallKey(hklm32));
            }

            // HKCU (user-specific)
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                                         .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (hkcu != null)
                    list.AddRange(ReadUninstallKey(hkcu));
            }

            return list;
        }

        private static IEnumerable<string> ReadUninstallKey(RegistryKey root)
        {
            var result = new List<string>();
            if (root == null) return result;

            foreach (var sub in SafeNames(root.GetSubKeyNames()))
            {
                try
                {
                    using var sk = root.OpenSubKey(sub);
                    if (sk == null) continue;

                    string? displayName = sk.GetValue("DisplayName")?.ToString();
            
                    // Tüm filtreleri kaldır - sadece DisplayName kontrolü yap
                    bool hasDisplayName = !string.IsNullOrWhiteSpace(displayName);

                    if (hasDisplayName)
                    {
                        result.Add(NormalizeAppName(displayName ?? ""));
                    }
                }
                catch
                {
                    // Log individual key errors but continue
                    // Registry key read failures are not critical
                }
            }

            return result;
        }

        /// <summary>
        /// Read UWP applications
        /// </summary>
        private async Task<IEnumerable<string>> ReadUwpApplicationsAsync()
        {
            return await Task.Run(() =>
            {
                var apps = new List<string>();
                
                try
                {
                    var pm = new PackageManager();
                    string? userSid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;

                    IEnumerable<Package>? packages = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(userSid))
                            packages = pm.FindPackagesForUser(userSid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to find packages for user, falling back to machine scope");
                    }
                    packages ??= pm.FindPackages();

                    foreach (var pkg in packages)
                    {
                        try
                        {
                            // Tüm filtreleri kaldır - sadece isim kontrolü yap
                            var name = pkg.Id?.Name;
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            // Get display name from manifest
                            string? displayName = GetDisplayNameFromManifest(pkg);
                            displayName ??= pkg.DisplayName;
                            
                            if (!string.IsNullOrWhiteSpace(displayName) &&
                                displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                            {
                                displayName = displayName.Substring("ms-resource:".Length).TrimStart('/');
                            }
                            displayName ??= name;

                            if (!string.IsNullOrWhiteSpace(displayName))
                                apps.Add(displayName.Trim());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to process UWP package {Name}", pkg.Id?.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read UWP applications");
                }

                return apps;
            });
        }

        /// <summary>
        /// Get display name from UWP manifest
        /// </summary>
        private static string? GetDisplayNameFromManifest(Package pkg)
        {
            try
            {
                var folder = pkg.InstalledLocation;
                var path = folder?.Path;
                var manifestPath = string.IsNullOrEmpty(path) ? null : Path.Combine(path, "AppxManifest.xml");
                
                if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
                {
                    var xdoc = XDocument.Load(manifestPath);
                    var ns = xdoc.Root?.Name.Namespace ?? XNamespace.None;
                    var props = xdoc.Root?.Element(ns + "Properties");
                    return props?.Element(ns + "DisplayName")?.Value;
                }
            }
            catch
            {
                // Manifest read failure is not critical
            }
            
            return null;
        }

        /// <summary>
        /// Save applications to JSON file
        /// </summary>
        private async Task SaveApplicationsToFileAsync(List<string> apps, Guid runId, CancellationToken ct)
        {
            try
            {
                var appsInfo = new
                {
                    collected_at = DateTimeOffset.Now,
                    count = apps.Count,
                    apps = apps.ToArray(),
                    run_id = runId
                };

                Directory.CreateDirectory(_systemDir);
                var appsPath = Path.Combine(_systemDir, "installed-apps.json");
                await File.WriteAllTextAsync(appsPath,
                    JsonSerializer.Serialize(appsInfo, new JsonSerializerOptions { WriteIndented = true }),
                    ct);

                _logger.LogDebug("Applications saved to {Path}", appsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save applications to file");
                throw;
            }
        }

        // Helper methods
        private static IEnumerable<string> SafeNames(string[] names) => names ?? Array.Empty<string>();
        private static string NormalizeAppName(string s) => string.IsNullOrWhiteSpace(s) ? s : s.Trim();
        
        private static bool IsDwordOne(object val)
        {
            if (val == null) return false;
            try
            {
                if (val is int i) return i == 1;
                if (val is string s) return s.Trim() == "1";
            }
            catch { }
            return false;
        }

        private static bool IsUpdateHotfix(string releaseType)
        {
            if (string.IsNullOrEmpty(releaseType)) return false;
            return releaseType.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0
                || releaseType.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0
                || releaseType.IndexOf("Security Update", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
