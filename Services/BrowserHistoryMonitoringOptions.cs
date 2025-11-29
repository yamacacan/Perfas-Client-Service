namespace WorkerService1.Services
{
    /// <summary>
    /// Browser history gerçek zamanlı izleme yapılandırma seçenekleri
    /// Basitleştirilmiş versiyon - sadece URL toplama için
    /// </summary>
    public class BrowserHistoryMonitoringOptions
    {
        /// <summary>
        /// Debounce gecikmesi (milisaniye) - Event storm yönetimi için
        /// Varsayılan: 1000ms (1 saniye)
        /// </summary>
        public int DebounceDelayMs { get; set; } = 1000;

        /// <summary>
        /// SQLite commit işleminin tamamlanması için bekleme süresi
        /// Varsayılan: 300ms
        /// </summary>
        public int SqliteCommitDelayMs { get; set; } = 300;

        /// <summary>
        /// Son kaç saniyelik URL'leri al
        /// Varsayılan: 30 saniye
        /// </summary>
        public int RecentUrlsSeconds { get; set; } = 30;

        /// <summary>
        /// Retry işlemi için maksimum deneme sayısı
        /// Varsayılan: 3
        /// </summary>
        public int MaxRetryCount { get; set; } = 3;

        /// <summary>
        /// Retry işlemleri arası bekleme süresi (milisaniye)
        /// Varsayılan: 100ms
        /// </summary>
        public int RetryDelayMs { get; set; } = 100;

        /// <summary>
        /// WAL dosyalarını izle (önerilen)
        /// Varsayılan: true
        /// </summary>
        public bool MonitorWalFiles { get; set; } = true;

        /// <summary>
        /// Gerçek zamanlı tarayıcı izlemeyi aktif et
        /// Varsayılan: true
        /// </summary>
        public bool EnableRealTimeMonitoring { get; set; } = true;
    }
}

