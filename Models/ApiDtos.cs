using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WorkerService1.Models
{
    /// <summary>
    /// AES ile şifrelenmeden önceki JSON yapısı: { "type": "...", "data": [...] }
    /// </summary>
    public class EncryptedDataWrapper
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public object Data { get; set; } = new List<object>();
    }

    public class ActivityDto
    {
        [JsonPropertyName("evt")]
        public string? Evt { get; set; }

        [JsonPropertyName("activity_type")]
        public string ActivityType { get; set; } = "APP_USE";

        [JsonPropertyName("process_name")]
        public string ProcessName { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("start_time_utc")]
        public string StartTimeUtc { get; set; } = string.Empty;

        [JsonPropertyName("end_time_utc")]
        public string? EndTimeUtc { get; set; }

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("user_sid")]
        public string? UserSid { get; set; }

        [JsonPropertyName("motherboard_uuid")]
        public string? MotherboardUuid { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("browser")]
        public string? Browser { get; set; }

        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("created_at_utc")]
        public string CreatedAtUtc { get; set; } = string.Empty;
    }

    public class BrowserDto
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("browser")]
        public string Browser { get; set; } = string.Empty;

        [JsonPropertyName("visit_time_utc")]
        public string VisitTimeUtc { get; set; } = string.Empty;

        [JsonPropertyName("created_at_utc")]
        public string CreatedAtUtc { get; set; } = string.Empty;

        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("user_sid")]
        public string? UserSid { get; set; }

        [JsonPropertyName("motherboard_uuid")]
        public string? MotherboardUuid { get; set; }
    }

    public class HardwareDto
    {
        [JsonPropertyName("hostname")]
        public string Hostname { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("user_sid")]
        public string UserSid { get; set; } = string.Empty;

        [JsonPropertyName("os_version")]
        public string? OsVersion { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("serial_number")]
        public string? SerialNumber { get; set; }

        [JsonPropertyName("motherboard_uuid")]
        public string? MotherboardUuid { get; set; }

        [JsonPropertyName("disk_total_gb")]
        public double? DiskTotalGb { get; set; }

        [JsonPropertyName("disk_used_gb")]
        public double? DiskUsedGb { get; set; }

        [JsonPropertyName("disk_free_gb")]
        public double? DiskFreeGb { get; set; }

        [JsonPropertyName("disk_usage_percent")]
        public double? DiskUsagePercent { get; set; }

        [JsonPropertyName("ram_total_gb")]
        public double? RamTotalGb { get; set; }

        [JsonPropertyName("ram_used_gb")]
        public double? RamUsedGb { get; set; }

        [JsonPropertyName("ram_free_gb")]
        public double? RamFreeGb { get; set; }

        [JsonPropertyName("ram_usage_percent")]
        public double? RamUsagePercent { get; set; }

        [JsonPropertyName("collected_at")]
        public string CollectedAt { get; set; } = string.Empty;
    }

    public class AppDto
    {
        [JsonPropertyName("app_name")]
        public string AppName { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("user_sid")]
        public string UserSid { get; set; } = string.Empty;

        [JsonPropertyName("motherboard_uuid")]
        public string MotherboardUuid { get; set; } = string.Empty;

        [JsonPropertyName("machine_name")]
        public string? MachineName { get; set; }

        [JsonPropertyName("collected_at")]
        public string CollectedAt { get; set; } = string.Empty;
    }

    public class IngestRequest
    {
        [JsonPropertyName("payload")]
        public string Payload { get; set; } = string.Empty;
    }
}
