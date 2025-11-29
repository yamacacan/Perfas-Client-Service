-- =====================================================
-- Performance Agent - MySQL Database Setup
-- =====================================================
-- Bu script, veritabanı ve tabloları oluşturur

-- Veritabanı oluştur
CREATE DATABASE IF NOT EXISTS performance_agent 
CHARACTER SET utf8mb4 
COLLATE utf8mb4_unicode_ci;

-- Veritabanını seç
USE performance_agent;

-- Activities tablosunu oluştur
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

-- Başarılı mesajı
SELECT 'Database and table created successfully!' as Status;

-- Tablo yapısını göster
DESCRIBE activities;

