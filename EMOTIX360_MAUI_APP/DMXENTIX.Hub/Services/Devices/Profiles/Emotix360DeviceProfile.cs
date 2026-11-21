using EMOTIX360.App.Models.Devices;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;

using AppDeviceType = EMOTIX360.App.Models.Devices.DeviceType;

namespace EMOTIX360.App.Services.Devices.Profiles;

/// <summary>
/// Advertisement identity and capabilities for EMOTIX360.
/// </summary>
public sealed class Emotix360DeviceProfile : IDeviceProfile
{
    public const string AdvertisedName = "EMOTIX360";

    public static readonly Guid ServiceUuid =
        Guid.Parse("4fafc201-1fb5-459e-8fcc-c5c9c331914b");

    public AppDeviceType DeviceType =>
        AppDeviceType.Emotix360;

    public string ProductName =>
        AdvertisedName;

    public DeviceCapabilities Capabilities =>
        DeviceCapabilities.AnimationControl |
        DeviceCapabilities.BrightnessControl |
        DeviceCapabilities.WifiProvisioning |
        DeviceCapabilities.MotionTelemetry |
        DeviceCapabilities.ClockStatus;

    /// <summary>
    /// Identifies EMOTIX360 using its advertised name and service UUID.
    /// If no service UUID list is supplied in the advertisement,
    /// the exact advertised name is used as a fallback.
    /// </summary>
    public bool TryMatch(
        IDevice device,
        out string advertisedName,
        out string matchDetails)
    {
        advertisedName = device.Name?.Trim() ?? string.Empty;
        matchDetails = string.Empty;

        // First check the advertised device name.
        if (!string.Equals(
                advertisedName,
                AdvertisedName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool hasServiceUuidRecords = false;
        bool hasExpectedServiceUuid = false;

        foreach (var record in device.AdvertisementRecords
                     ?? Array.Empty<AdvertisementRecord>())
        {
            // Check whether this record contains advertised service UUIDs.
            bool isServiceUuidRecord =
                record.Type is
                    AdvertisementRecordType.UuidsIncomplete16Bit or
                    AdvertisementRecordType.UuidsComplete16Bit or
                    AdvertisementRecordType.UuidsIncomplete32Bit or
                    AdvertisementRecordType.UuidsComplete32Bit or
                    AdvertisementRecordType.UuidsIncomplete128Bit or
                    AdvertisementRecordType.UuidsComplete128Bit;

            if (!isServiceUuidRecord)
                continue;

            hasServiceUuidRecords = true;

            // Our EMOTIX360 service UUID is 128-bit.
            bool is128BitUuidRecord =
                record.Type is
                    AdvertisementRecordType.UuidsIncomplete128Bit or
                    AdvertisementRecordType.UuidsComplete128Bit;

            if (!is128BitUuidRecord)
                continue;

            byte[] data = record.Data;

            // Each 128-bit UUID occupies 16 bytes.
            for (int offset = 0; offset + 16 <= data.Length; offset += 16)
            {
                Guid advertisedUuid =
                    new Guid(data.AsSpan(offset, 16), bigEndian: true);

                if (advertisedUuid == ServiceUuid)
                {
                    hasExpectedServiceUuid = true;
                    break;
                }
            }

            if (hasExpectedServiceUuid)
                break;
        }

        // If service UUID information is available, it must contain
        // the expected EMOTIX360 service UUID.
        if (hasServiceUuidRecords && !hasExpectedServiceUuid)
        {
            return false;
        }

        matchDetails = hasExpectedServiceUuid
            ? "Advertised name + service UUID"
            : "Exact advertised name; service UUID record unavailable";

        return true;
    }
}