using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WorkerService1.Services
{
    /// <summary>
    /// System state monitoring service for session events (lock, unlock, screen saver, sleep, shutdown, USB devices)
    /// Bu servis Windows sistem olaylarını izler ve kullanıcı oturumu değişikliklerini takip eder
    /// </summary>
    public class SystemStateService : IDisposable
    {
        // Servis bağımlılıkları
        private readonly ILogger<SystemStateService> _logger;
        private readonly string _activitiesDir; // Activity loglarının kaydedileceği dizin
        private readonly bool _enableConsoleOutput; // Konsol çıktısı aktif mi?
        private readonly string _username;
        private readonly string _domain;

        // Windows Terminal Services (WTS) sabitleri - oturum durumları için
        private const int WTS_SESSION_LOCK = 0x7;   // Oturum kilitlendi
        private const int WTS_SESSION_UNLOCK = 0x8; // Oturum kilidi açıldı
        private const int WTS_SESSION_LOGON = 0x5;   // Kullanıcı giriş yaptı
        private const int WTS_SESSION_LOGOFF = 0x6;  // Kullanıcı çıkış yaptı

        // Sistem durumu takibi için değişkenler
        private DateTime _lastStateChange = DateTime.Now; // Son durum kontrolü zamanı
        private bool _isLocked = false;                    // Sistem kilitli mi?
        private bool _isScreenSaverActive = false;         // Ekran koruyucu aktif mi?
        
        // USB cihaz izleme için WMI watchers
        private ManagementEventWatcher? _usbInsertWatcher;
        private ManagementEventWatcher? _usbRemoveWatcher;

        /// <summary>
        /// Constructor - servis başlatma
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="activitiesDir">Activity loglarının kaydedileceği dizin</param>
        /// <param name="username">Kullanıcı adı</param>
        /// <param name="domain">Domain adı</param>
        /// <param name="enableConsoleOutput">Konsol çıktısı aktif mi?</param>
        public SystemStateService(
            ILogger<SystemStateService> logger, 
            string activitiesDir,
            string username,
            string domain,
            bool enableConsoleOutput = true)
        {
            _logger = logger;
            _activitiesDir = activitiesDir;
            _username = username;
            _domain = domain;
            _enableConsoleOutput = enableConsoleOutput;
        }

        /// <summary>
        /// Sistem durumu izlemeyi başlat
        /// Bu method Worker servisi tarafından çağrılır
        /// </summary>
        /// <param name="ct">İptal token'ı</param>
        public async Task InitializeAsync(CancellationToken ct)
        {
            try
            {
                // Log dizinini oluştur
                Directory.CreateDirectory(_activitiesDir);

                // Başlangıç durumunu logla
                await LogSystemStateEventAsync("system_startup", "System monitoring started", ct);

                // Windows sistem olaylarını dinlemeye başla
                SetupSystemEventMonitoring();

                _logger.LogInformation("System state monitoring initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize system state monitoring");
                throw;
            }
        }

        /// <summary>
        /// Sistem durumu olayını logla
        /// Tüm sistem olayları bu method üzerinden kaydedilir
        /// </summary>
        /// <param name="eventType">Olay tipi (session_lock, screen_saver_start vb.)</param>
        /// <param name="description">Olay açıklaması</param>
        /// <param name="ct">İptal token'ı</param>
        public async Task LogSystemStateEventAsync(string eventType, string description, CancellationToken ct)
        {
            try
            {
                // Activity formatında olay verisi oluştur
                var stateEvent = new
                {
                    evt = "system_state",
                    event_type = eventType,
                    description = description,
                    timestamp = DateTimeOffset.Now,
                    username = _username,
                    domain = _domain
                };

                // Dosyaya yaz
                await WriteToActivityLogAsync(stateEvent, ct);

                // Konsol çıktısı aktifse logla
                if (_enableConsoleOutput)
                {
                    _logger.LogInformation("System State: {EventType} - {Description}", eventType, description);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log system state event");
            }
        }

        /// <summary>
        /// Mevcut sistem durumunu kontrol et
        /// Bu method periyodik olarak çağrılır (Worker.cs'de 30 saniyede bir)
        /// </summary>
        /// <param name="ct">İptal token'ı</param>
        public async Task CheckSystemStateAsync(CancellationToken ct)
        {
            try
            {
                var currentTime = DateTime.Now;
                var timeSinceLastCheck = currentTime - _lastStateChange;

                // Ekran koruyucu durumunu kontrol et
                var screenSaverActive = IsScreenSaverActive();
                if (screenSaverActive != _isScreenSaverActive)
                {
                    _isScreenSaverActive = screenSaverActive;
                    var eventType = screenSaverActive ? "screen_saver_start" : "screen_saver_stop";
                    var description = screenSaverActive ? "Screen saver activated" : "Screen saver deactivated";
                    await LogSystemStateEventAsync(eventType, description, ct);
                }

                // İş istasyonu kilit durumunu kontrol et
                var isLocked = IsWorkstationLocked();
                if (isLocked != _isLocked)
                {
                    _isLocked = isLocked;
                    var eventType = isLocked ? "workstation_locked" : "workstation_unlocked";
                    var description = isLocked ? "Workstation locked" : "Workstation unlocked";
                    await LogSystemStateEventAsync(eventType, description, ct);
                }

                // Son kontrol zamanını güncelle
                _lastStateChange = currentTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check system state");
            }
        }

        /// <summary>
        /// Windows sistem olaylarını dinlemeye başla
        /// SystemEvents static sınıfını kullanarak gerçek zamanlı olay dinleme
        /// </summary>
        private void SetupSystemEventMonitoring()
        {
            try
            {
                // Windows sistem olaylarına abone ol (static olaylar)
                SystemEvents.SessionSwitch += OnSessionSwitch;  // Oturum değişiklikleri
                SystemEvents.SessionEnding += OnSessionEnding; // Oturum sonlanıyor
                SystemEvents.SessionEnded += OnSessionEnded;   // Oturum sonlandı
                SystemEvents.PowerModeChanged += OnPowerModeChanged; // Güç durumu değişiklikleri (uyku, uyanma)

                // USB cihaz izlemeyi başlat
                SetupUsbDeviceMonitoring();

                _logger.LogDebug("System event monitoring setup completed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to setup system event monitoring");
            }
        }






        /// <summary>
        /// Oturum değişiklik olaylarını işle
        /// Windows'tan gelen SessionSwitch olaylarını yakalar
        /// </summary>
        /// <param name="sender">Olay göndericisi</param>
        /// <param name="e">Oturum değişiklik argümanları</param>
        private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                // Oturum değişiklik tipine göre olay tipini belirle
                var eventType = e.Reason switch
                {
                    SessionSwitchReason.SessionLock => "session_lock",
                    SessionSwitchReason.SessionUnlock => "session_unlock",
                    SessionSwitchReason.SessionLogon => "session_logon",
                    SessionSwitchReason.SessionLogoff => "session_logoff",
                    SessionSwitchReason.RemoteConnect => "session_connect",
                    SessionSwitchReason.RemoteDisconnect => "session_disconnect",
                    _ => "session_switch"
                };

                // Olay açıklamasını belirle
                var description = e.Reason switch
                {
                    SessionSwitchReason.SessionLock => "Session locked",
                    SessionSwitchReason.SessionUnlock => "Session unlocked",
                    SessionSwitchReason.SessionLogon => "User logged on",
                    SessionSwitchReason.SessionLogoff => "User logged off",
                    SessionSwitchReason.RemoteConnect => "Session connected",
                    SessionSwitchReason.RemoteDisconnect => "Session disconnected",
                    _ => $"Session switched: {e.Reason}"
                };

                // Kilit durumunu güncelle
                _isLocked = e.Reason == SessionSwitchReason.SessionLock;
                
                // Olayı logla
                await LogSystemStateEventAsync(eventType, description, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle session switch");
            }
        }


        /// <summary>
        /// Oturum sonlanma olaylarını işle
        /// Sistem kapatılırken veya kullanıcı çıkış yaparken tetiklenir
        /// </summary>
        /// <param name="sender">Olay göndericisi</param>
        /// <param name="e">Oturum sonlanma argümanları</param>
        private async void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try
            {
                var eventType = e.Reason == SessionEndReasons.SystemShutdown ? "system_shutdown" : "session_ending";
                var description = e.Reason == SessionEndReasons.SystemShutdown ? "System shutting down" : $"Session ending: {e.Reason}";
                
                await LogSystemStateEventAsync(eventType, description, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle session ending");
            }
        }

        /// <summary>
        /// Oturum sonlandı olaylarını işle
        /// Oturum tamamen sonlandıktan sonra tetiklenir
        /// </summary>
        /// <param name="sender">Olay göndericisi</param>
        /// <param name="e">Oturum sonlandı argümanları</param>
        private async void OnSessionEnded(object sender, SessionEndedEventArgs e)
        {
            try
            {
                var eventType = "session_ended";
                var description = $"Session ended: {e.Reason}";
                
                await LogSystemStateEventAsync(eventType, description, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle session ended");
            }
        }

        /// <summary>
        /// Güç durumu değişiklik olaylarını işle (uyku modu, uyanma)
        /// </summary>
        /// <param name="sender">Olay göndericisi</param>
        /// <param name="e">Güç durumu argümanları</param>
        private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                var eventType = e.Mode switch
                {
                    PowerModes.Suspend => "system_sleep",
                    PowerModes.Resume => "system_resume",
                    PowerModes.StatusChange => "power_status_change",
                    _ => "power_mode_change"
                };

                var description = e.Mode switch
                {
                    PowerModes.Suspend => "System entering sleep mode",
                    PowerModes.Resume => "System resuming from sleep",
                    PowerModes.StatusChange => "Power status changed",
                    _ => $"Power mode changed: {e.Mode}"
                };

                await LogSystemStateEventAsync(eventType, description, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle power mode change");
            }
        }

        /// <summary>
        /// USB cihaz izlemeyi kur - WMI kullanarak USB takılma/çıkarma olaylarını dinle
        /// </summary>
        private void SetupUsbDeviceMonitoring()
        {
            try
            {
                // USB cihaz takılma olayını dinle
                var insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
                _usbInsertWatcher = new ManagementEventWatcher(insertQuery);
                _usbInsertWatcher.EventArrived += OnUsbDeviceInserted;
                _usbInsertWatcher.Start();

                // USB cihaz çıkarma olayını dinle
                var removeQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
                _usbRemoveWatcher = new ManagementEventWatcher(removeQuery);
                _usbRemoveWatcher.EventArrived += OnUsbDeviceRemoved;
                _usbRemoveWatcher.Start();

                _logger.LogDebug("USB device monitoring setup completed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to setup USB device monitoring");
            }
        }

        /// <summary>
        /// USB cihaz takıldığında tetiklenir
        /// </summary>
        private async void OnUsbDeviceInserted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var deviceInfo = GetUsbDeviceInfo(e);
                var description = !string.IsNullOrEmpty(deviceInfo) 
                    ? $"USB device connected: {deviceInfo}" 
                    : "USB device connected";

                await LogSystemStateEventAsync("usb_device_connected", description, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle USB device insertion");
            }
        }

        /// <summary>
        /// USB cihaz çıkarıldığında tetiklenir
        /// </summary>
        private async void OnUsbDeviceRemoved(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var deviceInfo = GetUsbDeviceInfo(e);
                var description = !string.IsNullOrEmpty(deviceInfo) 
                    ? $"USB device disconnected: {deviceInfo}" 
                    : "USB device disconnected";

                await LogSystemStateEventAsync("usb_device_disconnected", description, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle USB device removal");
            }
        }

        /// <summary>
        /// USB cihaz bilgilerini al
        /// </summary>
        private string GetUsbDeviceInfo(EventArrivedEventArgs e)
        {
            try
            {
                var targetInstance = e.NewEvent.Properties["TargetInstance"].Value as ManagementBaseObject;
                if (targetInstance != null)
                {
                    var dependent = targetInstance.Properties["Dependent"].Value?.ToString();
                    if (!string.IsNullOrEmpty(dependent))
                    {
                        // PNP Device ID'den cihaz bilgisini çıkar
                        var deviceId = dependent.Split('=').LastOrDefault()?.Trim('"');
                        
                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            // Cihaz bilgilerini daha detaylı almaya çalış
                            try
                            {
                                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{deviceId.Replace("\\", "\\\\")}'");
                                using var results = searcher.Get();
                                
                                foreach (ManagementObject obj in results)
                                {
                                    var name = obj["Name"]?.ToString();
                                    var description = obj["Description"]?.ToString();
                                    var manufacturer = obj["Manufacturer"]?.ToString();
                                    
                                    if (!string.IsNullOrEmpty(name))
                                    {
                                        return manufacturer != null ? $"{name} ({manufacturer})" : name;
                                    }
                                    else if (!string.IsNullOrEmpty(description))
                                    {
                                        return description;
                                    }
                                }
                            }
                            catch
                            {
                                // Detaylı bilgi alınamazsa sadece device ID'yi döndür
                                return deviceId;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get USB device info");
            }

            return string.Empty;
        }


        /// <summary>
        /// İş istasyonunun kilitli olup olmadığını kontrol et
        /// Windows API kullanarak desktop erişimini test eder
        /// </summary>
        /// <returns>true = kilitli, false = açık</returns>
        private static bool IsWorkstationLocked()
        {
            try
            {
                // Default desktop'a erişmeye çalış
                var desktop = OpenDesktop("default", 0, false, DESKTOP_SWITCHDESKTOP);
                if (desktop == IntPtr.Zero)
                {
                    return true; // Desktop'a erişemiyorsak muhtemelen kilitli
                }
                
                // Desktop'ı kapat ve false döndür
                CloseDesktop(desktop);
                return false;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// Sistem durumu olayını activity loglarına yaz
        /// Activity logları ile aynı dosyaya yazar (YYYY-MM-DD.jsonl)
        /// </summary>
        /// <param name="data">Yazılacak veri</param>
        /// <param name="ct">İptal token'ı</param>
        private async Task WriteToActivityLogAsync(object data, CancellationToken ct)
        {
            try
            {
                // JSON formatına çevir ve yeni satır ekle
                var json = JsonSerializer.Serialize(data) + Environment.NewLine;
                
                // Günlük dosya adı oluştur (activity logları ile aynı formatta)
                var dailyFile = Path.Combine(_activitiesDir, $"{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
                
                // Dosyaya ekle
                await File.AppendAllTextAsync(dailyFile, json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write system state log");
            }
        }

        // Windows API fonksiyonları - P/Invoke ile çağrılır
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex); // Sistem metrikleri

        [DllImport("user32.dll")]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess); // Desktop aç

        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr hDesktop); // Desktop kapat


        /// <summary>
        /// Kaynakları temizle ve olay dinlemeyi durdur
        /// Servis kapatılırken çağrılır
        /// </summary>
        public void Dispose()
        {
            try
            {
                // Static olaylardan aboneliği iptal et
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.SessionEnding -= OnSessionEnding;
                SystemEvents.SessionEnded -= OnSessionEnded;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;

                // USB watcher'ları durdur ve dispose et
                if (_usbInsertWatcher != null)
                {
                    _usbInsertWatcher.Stop();
                    _usbInsertWatcher.Dispose();
                }

                if (_usbRemoveWatcher != null)
                {
                    _usbRemoveWatcher.Stop();
                    _usbRemoveWatcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing SystemStateService");
            }
        }

        // Desktop erişim izinleri
        private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

        /// <summary>
        /// Ekran koruyucunun aktif olup olmadığını kontrol et
        /// İki yöntem kullanır: 1) Ekran koruyucu process'lerini kontrol et, 2) İş istasyonu kilit durumu
        /// </summary>
        /// <returns>true = ekran koruyucu aktif, false = aktif değil</returns>
        private static bool IsScreenSaverActive()
        {
            try
            {
                // Yöntem 1: Ekran koruyucu process'lerini kontrol et
                var processes = System.Diagnostics.Process.GetProcesses();
                var screenSaverProcesses = processes.Where(p => 
                    p.ProcessName.Contains("scrnsave") ||     // Genel ekran koruyucu
                    p.ProcessName.Contains("screensaver") ||   // Ekran koruyucu
                    p.ProcessName.Contains("logon.scr") ||    // Giriş ekranı
                    p.ProcessName.Contains("ssmypics.scr") ||  // Resim ekran koruyucu
                    p.ProcessName.Contains("ssmyst.scr") ||    // Mistery ekran koruyucu
                    p.ProcessName.Contains("ssmarque.scr") ||  // Marquee ekran koruyucu
                    p.ProcessName.Contains("ssflwbox.scr") ||  // Floating box
                    p.ProcessName.Contains("sspipes.scr") ||   // Pipes ekran koruyucu
                    p.ProcessName.Contains("ssstars.scr") ||   // Stars ekran koruyucu
                    p.ProcessName.Contains("sstext3d.scr") ||  // 3D text
                    p.ProcessName.Contains("ss3dfoil.scr") ||  // 3D foil
                    p.ProcessName.Contains("ssbezier.scr") ||  // Bezier
                    p.ProcessName.Contains("ssmatrix.scr") ||  // Matrix
                    p.ProcessName.Contains("ssmaze.scr")       // Maze
                ).ToArray();

                // Process'leri temizle
                foreach (var process in screenSaverProcesses)
                {
                    process.Dispose();
                }

                // Ekran koruyucu process'i bulundu mu?
                if (screenSaverProcesses.Length > 0)
                {
                    return true;
                }

                // Yöntem 2: İş istasyonu kilit durumunu kontrol et (alternatif yaklaşım)
                return IsWorkstationLocked();
            }
            catch
            {
                return false;
            }
        }

    }
}
