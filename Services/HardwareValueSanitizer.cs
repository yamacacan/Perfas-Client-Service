using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WorkerService1.Services
{
    /// <summary>
    /// WMI/registry'den gelen üretici, model ve seri numarası değerlerini temizler;
    /// "To Be Filled By O.E.M." gibi anlamsız değerleri eler. Saf mantık, bağımlılık yok.
    /// </summary>
    public static class HardwareValueSanitizer
    {
        // Donanım placeholder'ları - geçersiz donanım bilgilerini filtrelemek için
        private static readonly HashSet<string> HardwarePlaceholders = new(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty,
            "unknown",
            "not available",
            "n/a",
            "na",
            "not applicable",
            "to be filled by o.e.m.",
            "system manufacturer",
            "system product name",
            "default string",
            "default",
            "desktop",
            "laptop",
            "computer",
            "pc",
            "workstation"
        };

        // Seri numarası placeholder'ları - geçersiz seri numaralarını filtrelemek için
        private static readonly HashSet<string> SerialPlaceholders = new(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty,
            "unknown",
            "not available",
            "n/a",
            "na",
            "not applicable",
            "to be filled by o.e.m.",
            "serial number",
            "serialnumber",
            "default string",
            "default"
        };

        public static string? SelectHardwareCandidate(IEnumerable<string?> candidates, string machineName)
        {
            foreach (var candidate in candidates)
            {
                var cleaned = CleanHardwareValue(candidate, machineName);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    return cleaned;
                }
            }

            return null;
        }

        public static string? SelectSerialCandidate(IEnumerable<string?> candidates, string machineName)
        {
            foreach (var candidate in candidates)
            {
                var cleaned = CleanSerialValue(candidate, machineName);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    return cleaned;
                }
            }

            return null;
        }



        private static string? CleanHardwareValue(string? value, string machineName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim();
            if (HardwarePlaceholders.Contains(normalized))
            {
                return null;
            }

            // Check against machine name - only filter if it's exactly the same
            if (string.Equals(normalized, machineName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Don't filter partial matches with machine name for hardware info
            // as legitimate hardware names might contain similar patterns

            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user))
            {
                // Exact match with username
                if (string.Equals(normalized, user, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // Partial match with username - be more conservative
                if (normalized.Length > 3 && normalized.Contains(user, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var domain = Environment.UserDomainName;
            if (!string.IsNullOrWhiteSpace(domain))
            {
                if (string.Equals(normalized, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (normalized.Length > 3 && normalized.Contains(domain, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var domainUser = $"{domain}\\{user}";
                if (string.Equals(normalized, domainUser, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            // Check for common generic patterns
            if (IsGenericHardwareName(normalized))
            {
                return null;
            }

            return normalized;
        }

        private static string? CleanSerialValue(string? value, string machineName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim();
            if (SerialPlaceholders.Contains(normalized))
            {
                return null;
            }

            // Check against machine name - only filter if it's exactly the same
            if (string.Equals(normalized, machineName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Don't filter partial matches with machine name for serial numbers
            // as legitimate serial numbers might contain similar patterns

            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user))
            {
                // Exact match with username
                if (string.Equals(normalized, user, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // Partial match with username - be more conservative
                if (normalized.Length > 3 && normalized.Contains(user, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var domain = Environment.UserDomainName;
            if (!string.IsNullOrWhiteSpace(domain))
            {
                if (string.Equals(normalized, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (normalized.Length > 3 && normalized.Contains(domain, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var domainUser = $"{domain}\\{user}";
                if (string.Equals(normalized, domainUser, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            // Check for empty GUID
            if (Guid.TryParse(normalized, out var parsedGuid) && parsedGuid == Guid.Empty)
            {
                return null;
            }

            // Check for uniform characters
            if (IsUniformCharacter(normalized, '0') || IsUniformCharacter(normalized, 'F') || IsUniformCharacter(normalized, 'X'))
            {
                return null;
            }

            // Check for generic serial patterns
            if (IsGenericSerialNumber(normalized))
            {
                return null;
            }

            return normalized;
        }

        private static bool IsGenericSerialNumber(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var lowerValue = value.ToLowerInvariant();

            // Check for common generic serial patterns
            var genericPatterns = new[]
            {
                "serial", "number", "sn", "s/n", "serialnumber", "serial_number"
            };

            foreach (var pattern in genericPatterns)
            {
                if (lowerValue.Contains(pattern))
                {
                    return true;
                }
            }

            // Check if it's too short (likely not a real serial)
            if (value.Length < 3)
            {
                return true;
            }

            // Check if it's mostly the same character
            if (value.All(c => c == value[0]))
            {
                return true;
            }

            return false;
        }

        private static bool IsGenericHardwareName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var lowerValue = value.ToLowerInvariant();

            // Check for common generic patterns - be more specific
            var genericPatterns = new[]
            {
                "pc", "desktop", "laptop", "computer", "workstation", "machine",
                "system", "device", "unit", "box", "tower", "case", "unknown",
                "not available", "n/a", "na", "not applicable", "default"
            };

            // Only consider it generic if it's exactly one of these patterns
            // or if it's very short and contains only generic terms
            if (genericPatterns.Contains(lowerValue))
            {
                return true;
            }

            // Check if it's mostly numbers or generic characters AND very short
            if (value.Length < 5 && lowerValue.All(c => char.IsDigit(c) || c == '-' || c == '_' || c == '.'))
            {
                return true;
            }

            return false;
        }

        private static bool IsUniformCharacter(string value, char character)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (char.ToUpperInvariant(ch) != char.ToUpperInvariant(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
