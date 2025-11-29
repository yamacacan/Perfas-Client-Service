using System;
using System.Management;

namespace WorkerService1.Services
{
    /// <summary>
    /// Helper class for system and user identification
    /// </summary>
    public static class SystemIdentityHelper
    {
        /// <summary>
        /// Get current user SID (Security Identifier)
        /// </summary>
        public static string GetCurrentUserSid()
        {
            try
            {
                return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Get motherboard UUID from WMI
        /// </summary>
        public static string GetMotherboardUuid()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT UUID FROM Win32_ComputerSystemProduct");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    var uuid = obj["UUID"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(uuid) &&
                        !uuid.Equals("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
                            StringComparison.OrdinalIgnoreCase) &&
                        !uuid.Equals("00000000-0000-0000-0000-000000000000",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return uuid;
                    }
                }
            }
            catch
            {
                // Ignore WMI errors
            }
            return "";
        }
    }
}
