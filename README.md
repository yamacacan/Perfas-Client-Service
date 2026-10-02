# 🖥️ Performance Agent - Windows Activity Monitoring Service

**Windows kullanıcı aktivitelerini ve tarayıcı geçmişini gerçek zamanlı izleyen .NET 8.0 background servisi.**

---

## 📋 Özellikler

### ✅ Aktivite İzleme
- **Window Activity Tracking** - Aktif pencere değişikliklerini kaydet
- **Browser History Monitoring** - Chrome, Edge, Firefox, Opera, Brave URL'lerini gerçek zamanlı yakala
- **System State Events** - Sistem kilidi, ekran koruyucu, uyku modu olayları

### ✅ Sistem Bilgileri
- Donanım bilgileri (CPU, RAM, Disk)
- İşletim sistemi ve kullanıcı bilgileri
- Yüklü uygulamalar (UWP ve Win32)

### ✅ Veri Depolama
- **Dual Storage** - JSON (JSONL) + SQLite
- **WAL Mode** - Yüksek performanslı veritabanı yazma
- **Otomatik Path Resolution** - Kullanıcıdan bağımsız çalışma

---

## 🔧 Sistem Gereksinimleri

| Gereksinim | Minimum | Önerilen |
|------------|---------|----------|
| **İşletim Sistemi** | Windows 10 (1809+) | Windows 10/11 |
| **.NET Runtime** | .NET 8.0 | .NET 8.0 |
| **Disk Alanı** | 100 MB | 500 MB |
| **RAM** | 256 MB | 512 MB |

---

## 📦 Kurulum

### 1️⃣ .NET 8.0 Runtime Yükle

**Yüklü mü kontrol et:**
```powershell
dotnet --version
```

**Yükle (Windows):**
```powershell
winget install Microsoft.DotNet.Runtime.8
```

**Veya Manuel:**
https://dotnet.microsoft.com/download/dotnet/8.0

---

### 2️⃣ Projeyi İndir

```bash
git clone <repository-url>
cd WorkerService1
```

---

### 3️⃣ Ayarları Yapılandır (Opsiyonel)

`appsettings.json` dosyasını düzenle:

```json
{
  "PerformanceAgent": {
    "PollIntervalMs": 1000,
    "DataDirectory": "",  // Boş bırak = Otomatik (%LOCALAPPDATA%\PerformanceAgent)
    "EnableConsoleOutput": true
  }
}
```

**Varsayılan veri dizini:**
```
C:\Users\<USERNAME>\AppData\Local\PerformanceAgent\
```

---

### 4️⃣ Çalıştır

#### Geliştirme Modu:
```powershell
dotnet run
```

#### Production Build:
```powershell
dotnet publish -c Release -r win-x64 --self-contained false
cd bin\Release\net8.0-windows10.0.19041.0\win-x64\publish
.\WorkerService1.exe
```

---

## 📂 Veri Yapısı

```
%LOCALAPPDATA%\PerformanceAgent\
├── data\
│   ├── activities\
│   │   ├── 2025-10-20.jsonl      # Günlük activity logları (JSON Lines)
│   │   └── ...
│   └── system\
│       ├── machine-info.json     # Sistem bilgileri
│       ├── user-info.json        # Kullanıcı bilgileri
│       └── installed-apps.json   # Yüklü uygulamalar
├── logs\
│   └── system-logs-*.json        # Sistem logları
└── activities.db                 # SQLite veritabanı
    ├── activities.db-wal         # Write-Ahead Log
    └── activities.db-shm         # Shared Memory Index
```

---

## 📊 Veri Formatları

### Activity Log (JSONL)
```json
{"evt":"activity","activity_type":"window","start":"2025-10-20T10:30:00Z","end":"2025-10-20T10:35:00Z","duration_ms":300000,"process":"chrome","title":"GitHub - Google Chrome","username":"user","domain":"MACHINE"}
{"evt":"activity","activity_type":"browser","start":"2025-10-20T10:31:15Z","end":"2025-10-20T10:31:15Z","duration_ms":0,"process":"Chrome","title":"GitHub Homepage","username":"user","domain":"MACHINE","url":"https://github.com","browser":"Chrome"}
```

### SQLite Schema
```sql
CREATE TABLE ActivityLogs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Evt TEXT NOT NULL DEFAULT 'activity',
    ActivityType TEXT NOT NULL,  -- 'window' or 'browser'
    ProcessName TEXT NOT NULL,
    Title TEXT,
    StartTimeUtc TEXT NOT NULL,
    EndTimeUtc TEXT NOT NULL,
    DurationMs INTEGER NOT NULL,
    Username TEXT NOT NULL,
    Domain TEXT NOT NULL,
    Url TEXT NULL,               -- Sadece browser aktiviteleri için
    Browser TEXT NULL,            -- Sadece browser aktiviteleri için
    CreatedAtUtc TEXT NOT NULL
);
```

---

## 🔧 Sorun Giderme

### ❌ ".NET 8.0 not found"
**Çözüm:** .NET 8.0 Runtime yükle
```powershell
winget install Microsoft.DotNet.Runtime.8
```

### ❌ "Access denied" / Yazma izni hatası
**Çözüm 1:** Uygulamayı yönetici olarak çalıştır
```powershell
# PowerShell Admin
.\WorkerService1.exe
```

**Çözüm 2:** Veri dizinini değiştir
```json
{
  "DataDirectory": "C:\\ProgramData\\PerformanceAgent"
}
```

### ❌ Browser history yakalanmıyor
**Olası Sebepler:**
1. **Browser yüklü değil** → Normal, sadece yüklü browser'lar izlenir
2. **Farklı profile kullanılıyor** → Uygulama otomatik profil taraması yapar
3. **Portable browser** → Standart path'lerde değil, desteklenmez

**Kontrol:**
```powershell
# Chrome history var mı?
Test-Path "$env:LOCALAPPDATA\Google\Chrome\User Data\Default\History"
```

### ❌ Antivirus uygulamayı engelliyor
**Çözüm:** Exception ekle
- Windows Defender → "Virus & threat protection" → "Exclusions"
- Klasör ekle: `%LOCALAPPDATA%\PerformanceAgent`

---

## 🏗️ Geliştirme

### Build
```powershell
dotnet build
```

### Test
```powershell
dotnet run
```

### Debug Logging Aç
`appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "WorkerService1": "Debug",
      "WorkerService1.Services.BrowserHistoryService": "Debug"
    }
  }
}
```

---

## 📄 Lisans

Bu proje özel/ticari kullanım için geliştirilmiştir.

---

## 🤝 Destek

Sorunlar için:
1. `logs/` klasöründeki hata loglarını kontrol et
2. GitHub Issues sayfasından yeni issue aç
3. System validation hatalarını paylaş:
   ```
   .\WorkerService1.exe > startup-log.txt 2>&1
   ```

---

## 🔒 Güvenlik & Gizlilik

- ✅ Tüm veriler **yerel** olarak saklanır
- ✅ Şifreleme yok (gerekirse eklenebilir)
- ✅ Ağ bağlantısı yok (data upload servisi eklenebilir)
- ⚠️ Hassas veriler içerebilir (URL'ler, window title'ları)

**Öneriler:**
- Paylaşılan bilgisayarlarda dikkatli kullan
- Backup alırken dosyaları şifrele
- Production'da veri retention policy uygula

---

## 📈 Performans

| Metrik | Değer |
|--------|-------|
| **CPU Kullanımı** | ~0.5-2% (idle) |
| **RAM Kullanımı** | ~50-100 MB |
| **Disk Yazma** | ~10-50 KB/dakika |
| **Event Response** | <100ms |
| **Browser Detection** | <2 saniye (debounce) |

---

## 🎯 Roadmap

- [ ] **Data Upload Service** - HTTP API ile veri gönderme
- [ ] **Encryption** - Hassas verileri şifrele
- [ ] **Web Dashboard** - Verileri görselleştir
- [ ] **Windows Service** - Arka planda sürekli çalış
- [ ] **Multi-platform** - Linux/Mac desteği (sınırlı)

---

**🚀 Hazır! Şimdi kullanmaya başlayabilirsin.**

```powershell
dotnet run
```


---

## Gizli Bilgiler (ApiUrl / ApiToken / AesKey)

Bu değerler repoda tutulmaz. Okunma sırası: HKLM\SOFTWARE\Perfas\PerformanceAgent (MSI yazar) → ortam değişkenleri `PERFAS_APIURL`, `PERFAS_APITOKEN`, `PERFAS_AESKEY` → appsettings.json. ApiUrl HTTPS olmalı, AesKey UTF-8 olarak tam 32 bayt olmalıdır; aksi halde senkronizasyon devre dışı kalır.

Testler: `dotnet test Tests`
