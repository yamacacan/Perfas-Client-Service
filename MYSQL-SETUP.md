# MySQL Kurulum Rehberi

Bu rehber, Performance Agent uygulamasının topladığı aktivite verilerini MySQL sunucusuna göndermek için gerekli adımları içerir.

---

## 📋 İçindekiler

1. [Gereksinimler](#gereksinimler)
2. [MySQL Sunucu Kurulumu](#mysql-sunucu-kurulumu)
3. [Veritabanı Oluşturma](#veritabanı-oluşturma)
4. [Kullanıcı ve İzinler](#kullanıcı-ve-izinler)
5. [Client Konfigürasyonu](#client-konfigürasyonu)
6. [Test ve Doğrulama](#test-ve-doğrulama)
7. [Sorun Giderme](#sorun-giderme)

---

## 🔧 Gereksinimler

### Sunucu Tarafı
- **MySQL Server 8.0+** veya **MariaDB 10.5+**
- **Windows Server**, **Linux** veya **macOS**
- Açık **3306** portu (varsayılan MySQL portu)

### Client Tarafı
- **.NET 8.0 Runtime**
- Ağ bağlantısı (MySQL sunucusuna erişim)

---

## 📦 MySQL Sunucu Kurulumu

### Windows için MySQL Kurulumu

#### 1. MySQL Community Server İndir
[https://dev.mysql.com/downloads/mysql/](https://dev.mysql.com/downloads/mysql/)

#### 2. Installer ile Kurulum
```bash
# MSI installer'ı çalıştır
# Setup Type: "Server only" veya "Developer Default"
# Authentication Method: "Use Strong Password Encryption"
# Root şifresi belirle (örn: "root123")
```

#### 3. MySQL Servisini Başlat
```powershell
# Servis durumunu kontrol et
Get-Service -Name MySQL80

# Servisi başlat
Start-Service -Name MySQL80

# Otomatik başlatma
Set-Service -Name MySQL80 -StartupType Automatic
```

### Linux (Ubuntu/Debian) için MySQL Kurulumu

```bash
# Paket listesini güncelle
sudo apt update

# MySQL Server kur
sudo apt install mysql-server -y

# MySQL servisini başlat
sudo systemctl start mysql
sudo systemctl enable mysql

# Güvenlik ayarları
sudo mysql_secure_installation
```

### XAMPP ile Hızlı Test Kurulumu (Önerilen - Test için)

```bash
# 1. XAMPP indir: https://www.apachefriends.org/
# 2. Kur ve XAMPP Control Panel'i aç
# 3. MySQL'i başlat (Start butonu)
# 4. "Admin" butonu ile phpMyAdmin'e git
```

---

## 🗄️ Veritabanı Oluşturma

### MySQL Komut Satırı ile

```bash
# MySQL'e root olarak bağlan
mysql -u root -p

# (Şifre: root şifreniz)
```

```sql
-- Veritabanı oluştur
CREATE DATABASE performance_agent CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

-- Veritabanını seç
USE performance_agent;

-- Tabloyu oluştur (otomatik oluşturulur ama manuel de oluşturabilirsiniz)
CREATE TABLE IF NOT EXISTS activities (
    id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    evt VARCHAR(50) NOT NULL DEFAULT 'activity',
    activity_type VARCHAR(50) NOT NULL DEFAULT 'window',
    process_name VARCHAR(255) NOT NULL,
    title TEXT NULL,
    start_time_utc DATETIME NOT NULL,
    end_time_utc DATETIME NOT NULL,
    duration_ms BIGINT NOT NULL,
    username VARCHAR(255) NOT NULL,
    domain VARCHAR(255) NOT NULL,
    url TEXT NULL,
    browser VARCHAR(100) NULL,
    created_at_utc DATETIME NOT NULL,
    received_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    
    INDEX idx_activity_type (activity_type),
    INDEX idx_process_name (process_name),
    INDEX idx_start_time (start_time_utc),
    INDEX idx_username (username),
    INDEX idx_browser (browser)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Kontrol et
DESCRIBE activities;
```

### phpMyAdmin ile (XAMPP kullanıyorsanız)

1. **phpMyAdmin'e Git:** `http://localhost/phpmyadmin`
2. **"New" (Yeni)** butonuna tıkla
3. **Database name:** `performance_agent`
4. **Collation:** `utf8mb4_unicode_ci`
5. **Create** butonuna tıkla
6. **SQL sekmesine** geç ve yukarıdaki `CREATE TABLE` komutunu yapıştır
7. **Go** butonuna tıkla

---

## 👤 Kullanıcı ve İzinler

### Uzaktan Erişim için Kullanıcı Oluştur (Önemli!)

```sql
-- Yeni kullanıcı oluştur (herhangi bir IP'den erişim)
CREATE USER 'agent_user'@'%' IDENTIFIED BY 'agent_password_123';

-- Veritabanı izinleri ver
GRANT SELECT, INSERT, UPDATE, DELETE ON performance_agent.* TO 'agent_user'@'%';

-- İzinleri yenile
FLUSH PRIVILEGES;

-- Kontrol et
SELECT User, Host FROM mysql.user WHERE User = 'agent_user';
```

### Root ile Test (Geliştirme için)

```sql
-- Root kullanıcısına uzaktan erişim izni ver (GÜVENSİZ - sadece test için!)
CREATE USER 'root'@'%' IDENTIFIED BY 'root123';
GRANT ALL PRIVILEGES ON *.* TO 'root'@'%' WITH GRANT OPTION;
FLUSH PRIVILEGES;
```

⚠️ **ÖNEMLİ:** Production ortamında mutlaka güvenli şifre ve sınırlı izinler kullanın!

---

## 🔧 Uzak Erişim Konfigürasyonu

### Windows MySQL Konfigürasyonu

**`C:\ProgramData\MySQL\MySQL Server 8.0\my.ini`** dosyasını düzenle:

```ini
[mysqld]
# Tüm IP'lerden bağlantı kabul et
bind-address = 0.0.0.0

# Port
port = 3306
```

**MySQL servisini yeniden başlat:**
```powershell
Restart-Service -Name MySQL80
```

### Linux MySQL Konfigürasyonu

**`/etc/mysql/mysql.conf.d/mysqld.cnf`** dosyasını düzenle:

```ini
[mysqld]
bind-address = 0.0.0.0
```

**MySQL'i yeniden başlat:**
```bash
sudo systemctl restart mysql
```

### Firewall Ayarları

#### Windows Firewall
```powershell
# MySQL portunu aç
New-NetFirewallRule -DisplayName "MySQL Server" -Direction Inbound -Protocol TCP -LocalPort 3306 -Action Allow
```

#### Linux Firewall (ufw)
```bash
sudo ufw allow 3306/tcp
sudo ufw reload
```

---

## ⚙️ Client Konfigürasyonu

### 1. `appsettings.json` Dosyasını Düzenle

```json
{
  "DataSync": {
    "Enabled": true,
    "MySqlHost": "192.168.1.100",      // MySQL sunucu IP adresi
    "MySqlPort": 3306,                  // MySQL portu
    "MySqlDatabase": "performance_agent",
    "MySqlUser": "agent_user",          // Veya "root" (test için)
    "MySqlPassword": "agent_password_123",
    "SyncIntervalMinutes": 1,           // Test için 1, production'da 10
    "BatchSize": 10,                    // Her seferde kaç kayıt gönderilsin
    "DeleteAfterSync": false            // Gönderilen kayıtları SQLite'dan sil
  }
}
```

### 2. Ayar Açıklamaları

| Ayar | Açıklama | Örnek Değer |
|------|----------|-------------|
| `Enabled` | Senkronizasyon aktif mi? | `true` |
| `MySqlHost` | MySQL sunucu IP/hostname | `192.168.1.100` veya `localhost` |
| `MySqlPort` | MySQL portu | `3306` |
| `MySqlDatabase` | Veritabanı adı | `performance_agent` |
| `MySqlUser` | MySQL kullanıcı adı | `agent_user` veya `root` |
| `MySqlPassword` | MySQL şifresi | `agent_password_123` |
| `SyncIntervalMinutes` | Senkronizasyon aralığı (dakika) | `1` (test), `10` (production) |
| `BatchSize` | Tek seferde gönderilecek kayıt sayısı | `10` (test), `100` (production) |
| `DeleteAfterSync` | Gönderildikten sonra SQLite'dan silinsin mi? | `false` (önerilen) |

---

## ✅ Test ve Doğrulama

### 1. Uygulamayı Başlat

```powershell
cd C:\Users\burak\OneDrive\Masaüstü\Proje-Deneme1\WorkerService1\WorkerService1
dotnet run
```

### 2. Logları İzle

Aşağıdaki mesajları arayın:
```
DataSyncService initialized - MySQL sync enabled (Interval: 1 min)
```

30 saniye sonra ilk sync denemesi:
```
Syncing 10 activities to MySQL
Successfully synced 10 activities to MySQL
```

### 3. MySQL'de Verileri Kontrol Et

```sql
-- MySQL'e bağlan
mysql -u agent_user -p

-- Veritabanını seç
USE performance_agent;

-- Kayıt sayısını kontrol et
SELECT COUNT(*) FROM activities;

-- Son 10 kaydı göster
SELECT id, activity_type, process_name, title, start_time_utc, browser
FROM activities
ORDER BY received_at DESC
LIMIT 10;

-- Browser kayıtlarını kontrol et
SELECT COUNT(*) as count, browser
FROM activities
WHERE browser IS NOT NULL
GROUP BY browser;
```

### 4. Bağlantıyı Test Et (Harici Araç)

**PowerShell ile:**
```powershell
# MySQL sunucusuna ping at
Test-NetConnection -ComputerName 192.168.1.100 -Port 3306
```

**MySQL Workbench ile:**
1. MySQL Workbench'i aç
2. **"+"** butonuna tıkla (New Connection)
3. **Connection Name:** `Performance Agent Test`
4. **Hostname:** `192.168.1.100`
5. **Port:** `3306`
6. **Username:** `agent_user`
7. **Password:** Store in Vault → `agent_password_123`
8. **Test Connection** butonuna tıkla

---

## 🐛 Sorun Giderme

### ❌ "MySQL connection test failed"

**Sebep 1: MySQL sunucusu çalışmıyor**
```bash
# Windows
Get-Service -Name MySQL80

# Linux
sudo systemctl status mysql
```

**Sebep 2: Firewall portu kapalı**
```powershell
# Windows - Port kontrolü
Test-NetConnection -ComputerName 192.168.1.100 -Port 3306
```

**Sebep 3: bind-address yanlış**
```bash
# my.ini veya my.cnf'i kontrol et
grep bind-address /etc/mysql/mysql.conf.d/mysqld.cnf
```

**Sebep 4: Kullanıcı izni yok**
```sql
-- MySQL'de kontrol et
SELECT User, Host FROM mysql.user WHERE User = 'agent_user';
SHOW GRANTS FOR 'agent_user'@'%';
```

---

### ❌ "Access denied for user"

**Şifre yanlış:**
```bash
# MySQL komut satırından test et
mysql -h 192.168.1.100 -u agent_user -p
# (Şifreyi gir)
```

**Kullanıcı mevcut değil:**
```sql
-- Kullanıcıyı tekrar oluştur
CREATE USER 'agent_user'@'%' IDENTIFIED BY 'agent_password_123';
GRANT SELECT, INSERT, UPDATE, DELETE ON performance_agent.* TO 'agent_user'@'%';
FLUSH PRIVILEGES;
```

---

### ❌ "No new activities to sync"

SQLite'da veri var mı kontrol et:
```powershell
# SQLite veritabanını aç
$dbPath = "$env:LOCALAPPDATA\PerformanceAgent\activities_v2.db"
sqlite3 $dbPath

# Senkronize edilmemiş kayıtları kontrol et
SELECT COUNT(*) FROM ActivityLogs WHERE Synced = 0 OR Synced IS NULL;

# Toplam kayıt sayısı
SELECT COUNT(*) FROM ActivityLogs;
```

---

### ❌ "Can't connect to MySQL server on '192.168.1.100' (10060)"

**Ağ bağlantısı yok:**
```powershell
# Ping at
ping 192.168.1.100

# Port kontrolü
Test-NetConnection -ComputerName 192.168.1.100 -Port 3306
```

**Windows Firewall:**
```powershell
# Gelen bağlantıları kontrol et
Get-NetFirewallRule | Where-Object {$_.LocalPort -eq 3306}
```

---

## 📊 Veri Görüntüleme (Basit SQL Sorguları)

### Günlük Özet
```sql
SELECT 
    DATE(start_time_utc) as date,
    COUNT(*) as total_activities,
    COUNT(DISTINCT username) as users,
    COUNT(CASE WHEN activity_type = 'browser' THEN 1 END) as browser_activities,
    COUNT(CASE WHEN activity_type = 'window' THEN 1 END) as window_activities
FROM activities
GROUP BY DATE(start_time_utc)
ORDER BY date DESC
LIMIT 7;
```

### En Çok Kullanılan Uygulamalar
```sql
SELECT 
    process_name,
    COUNT(*) as count,
    SUM(duration_ms) / 1000 / 60 as total_minutes
FROM activities
WHERE activity_type = 'window'
GROUP BY process_name
ORDER BY count DESC
LIMIT 10;
```

### En Çok Ziyaret Edilen Web Siteleri
```sql
SELECT 
    browser,
    COUNT(*) as visit_count,
    COUNT(DISTINCT url) as unique_urls
FROM activities
WHERE activity_type = 'browser' AND url IS NOT NULL
GROUP BY browser
ORDER BY visit_count DESC;
```

---

## 🚀 Production Ayarları

Production ortamında kullanırken:

```json
{
  "DataSync": {
    "Enabled": true,
    "MySqlHost": "production-db.yourcompany.com",
    "MySqlPort": 3306,
    "MySqlDatabase": "performance_agent",
    "MySqlUser": "agent_service",
    "MySqlPassword": "STRONG_PASSWORD_HERE",
    "SyncIntervalMinutes": 10,
    "BatchSize": 100,
    "DeleteAfterSync": false
  }
}
```

**Öneriler:**
- ✅ Güçlü şifreler kullanın
- ✅ Sınırlı izinlere sahip kullanıcı oluşturun
- ✅ SSL/TLS bağlantısı aktif edin
- ✅ MySQL sunucusunu güvenlik duvarı arkasına alın
- ✅ Düzenli yedekleme yapın
- ❌ Root kullanıcısını kullanmayın
- ❌ `bind-address = 0.0.0.0` production'da riskli olabilir

---

## 📚 Ek Kaynaklar

- [MySQL Official Documentation](https://dev.mysql.com/doc/)
- [MySQL Workbench](https://dev.mysql.com/downloads/workbench/)
- [XAMPP Download](https://www.apachefriends.org/)
- [HeidiSQL (Windows MySQL Client)](https://www.heidisql.com/)

---

## ✅ Kurulum Checklist

- [ ] MySQL Server kuruldu
- [ ] `performance_agent` veritabanı oluşturuldu
- [ ] `activities` tablosu oluşturuldu (veya otomatik oluşturulacak)
- [ ] Kullanıcı ve izinler ayarlandı
- [ ] `bind-address = 0.0.0.0` ayarlandı
- [ ] Firewall 3306 portu açıldı
- [ ] `appsettings.json` düzenlendi
- [ ] Uygulama başlatıldı ve loglar kontrol edildi
- [ ] MySQL'de veriler görüntülendi

---

**🎉 Kurulum tamamlandı! Artık tüm aktivite verileri MySQL sunucunuzda toplanacak.**

