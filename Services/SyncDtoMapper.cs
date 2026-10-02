using System;
using System.Collections.Generic;
using System.Linq;
using WorkerService1.Models;

namespace WorkerService1.Services
{
    /// <summary>Depolama modellerini API DTO'larına çevirir (saf dönüşüm, yan etkisiz).</summary>
    public static class SyncDtoMapper
    {
        private static string Fmt(DateTime d) => d.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss");

        public static List<ActivityDto> ToDtos(IEnumerable<ActivityLog> src) => src.Select(a => new ActivityDto
        {
            Evt = a.Evt,
            ActivityType = a.ActivityType == "window" ? "APP_USE" : a.ActivityType,
            ProcessName = a.ProcessName,
            Title = a.Title,
            StartTimeUtc = Fmt(a.StartTimeUtc),
            EndTimeUtc = Fmt(a.EndTimeUtc),
            DurationMs = a.DurationMs,
            Username = a.Username,
            Domain = a.Domain,
            UserSid = a.UserSid,
            MotherboardUuid = a.MotherboardUuid,
            Url = a.Url,
            Browser = a.Browser,
            BaseUrl = a.BaseUrl,
            CreatedAtUtc = Fmt(a.CreatedAtUtc)
        }).ToList();

        public static List<BrowserDto> ToDtos(IEnumerable<BrowserData> src, string userSid, string motherboardUuid) =>
            src.Select(b => new BrowserDto
            {
                Url = b.Url,
                Title = b.Title,
                Browser = b.Browser,
                VisitTimeUtc = Fmt(b.VisitTimeUtc),
                CreatedAtUtc = Fmt(b.CreatedAtUtc),
                BaseUrl = b.BaseUrl,
                Username = b.Username,
                Domain = b.Domain,
                // Servis SYSTEM olarak çalışır; kullanıcı SID'i kayıt anında ajan tarafından yazılmış olmalı.
                UserSid = string.IsNullOrEmpty(b.UserSid) ? userSid : b.UserSid,
                MotherboardUuid = motherboardUuid
            }).ToList();

        public static List<AppDto> ToDtos(IEnumerable<InstalledAppData> src) => src.Select(a => new AppDto
        {
            AppName = a.AppName,
            Username = a.Username,
            Domain = a.Domain,
            UserSid = a.UserSid,
            MotherboardUuid = a.MotherboardUuid,
            MachineName = a.MachineName,
            CollectedAt = Fmt(a.CollectedAt)
        }).ToList();

        public static List<HardwareDto> ToDtos(IEnumerable<SystemHardwareData> src) => src.Select(h => new HardwareDto
        {
            Hostname = h.Hostname,
            Username = h.Username,
            Domain = h.Domain,
            UserSid = h.UserSid,
            OsVersion = h.OsVersion,
            Model = h.Model,
            SerialNumber = h.SerialNumber,
            MotherboardUuid = h.MotherboardUuid,
            DiskTotalGb = h.DiskTotalGB,
            DiskUsedGb = h.DiskUsedGB,
            DiskFreeGb = h.DiskFreeGB,
            DiskUsagePercent = h.DiskUsagePercent,
            RamTotalGb = h.RamTotalGB,
            RamUsedGb = h.RamUsedGB,
            RamFreeGb = h.RamFreeGB,
            RamUsagePercent = h.RamUsagePercent,
            CollectedAt = Fmt(h.CollectedAt)
        }).ToList();
    }
}
