using System;

namespace WorkerService1.Services
{
    /// <summary>JSONL dosyasından okunan kaydın hangi dosyadan geldiğini izler.</summary>
    public interface ISourceTracked
    {
        string? SourceFile { get; set; }
    }

    #region Data Models with SourceFile tracking

    /// <summary>
    /// Activity log modeli - window/browser aktiviteleri için
    /// </summary>
    public class ActivityLog : ISourceTracked
    {
        public int Id { get; set; }
        public string? Evt { get; set; } = "activity";
        public string ActivityType { get; set; } = "window";
        public string ProcessName { get; set; } = "";
        public string? Title { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public DateTime EndTimeUtc { get; set; }
        public long DurationMs { get; set; }
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";
        public string UserSid { get; set; } = "";
        public string MotherboardUuid { get; set; } = "";
        public string? Url { get; set; }
        public string? Browser { get; set; }
        public string? BaseUrl { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        
        // JSONL tracking için
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourceFile { get; set; }
    }

    /// <summary>
    /// Browser data modeli
    /// </summary>
    public class BrowserData : ISourceTracked
    {
        public int Id { get; set; }
        public string Url { get; set; } = "";
        public string? Title { get; set; }
        public string Browser { get; set; } = "";
        public DateTime VisitTimeUtc { get; set; }
        public string? BaseUrl { get; set; }
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";
        public string UserSid { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourceFile { get; set; }
    }

    /// <summary>
    /// Installed app modeli
    /// </summary>
    public class InstalledAppData : ISourceTracked
    {
        public int Id { get; set; }
        public string AppName { get; set; } = "";
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";
        public string UserSid { get; set; } = "";
        public string MotherboardUuid { get; set; } = "";
        public string? MachineName { get; set; }
        public DateTime CollectedAt { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourceFile { get; set; }
    }

    /// <summary>
    /// System hardware modeli
    /// </summary>
    public class SystemHardwareData : ISourceTracked
    {
        public int Id { get; set; }
        public string Hostname { get; set; } = "";
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";
        public string UserSid { get; set; } = "";
        public string? OsVersion { get; set; }
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
        public DateTime CollectedAt { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourceFile { get; set; }
    }

    #endregion
}
