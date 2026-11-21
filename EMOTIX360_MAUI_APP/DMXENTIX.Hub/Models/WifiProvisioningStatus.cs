using System;
using System.Collections.Generic;
using System.Text;

namespace EMOTIX360.App.Models
{
    public sealed record WifiProvisioningStatus(
        string State,
        int StoredCount)
    {
        public const int MaximumStoredNetworks = 8;

        public static bool TryParse(
            string? payload,
            out WifiProvisioningStatus status)
        {
            status = null!;

            if (string.IsNullOrWhiteSpace(payload))
                return false;

            string[] fields = payload
                .Trim()
                .Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries);

            if (fields.Length != 2)
                return false;

            if (!fields[0].StartsWith(
                    "WCFG=",
                    StringComparison.Ordinal))
                return false;

            if (!fields[1].StartsWith(
                    "WCNT=",
                    StringComparison.Ordinal))
                return false;

            string state = fields[0][5..].ToUpperInvariant();

            if (state is not (
                "IDLE" or
                "RECEIVING" or
                "SAVED" or
                "CLEARED" or
                "ERROR"))
            {
                return false;
            }

            if (!int.TryParse(fields[1][5..], out int count))
                return false;

            if (count is < 0 or > MaximumStoredNetworks)
                return false;

            status = new WifiProvisioningStatus(state, count);

            return true;
        }
    }
}
