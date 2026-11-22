using DMXENTIX.Hub.Models.Devices;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;

using AppDeviceType =
    global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.Services.Devices.Profiles;

/// <summary>
/// Advertisement identity and capabilities for DMXENTIX SoundNode.
/// </summary>
public sealed class SoundNodeDeviceProfile : IDeviceProfile
{
    public const string AdvertisedName = "DMXENTIX-SOUND";

    public static readonly Guid ServiceUuid =
        Guid.Parse("d3601000-5a3b-4b3f-9c21-8f5a2d7c0101");

    public AppDeviceType DeviceType =>
        AppDeviceType.SoundNode;

    public string ProductName =>
        "DMXENTIX SoundNode";

    public DeviceCapabilities Capabilities =>
        DeviceCapabilities.AudioLevelTelemetry |
        DeviceCapabilities.ChipTemperatureTelemetry;

    /// <summary>
    /// Identifies SoundNode using its advertised name and service UUID.
    /// If the advertisement contains no service UUID records,
    /// the exact advertised name is used as a fallback.
    /// </summary>
    public bool TryMatch(
        IDevice device,
        out string advertisedName,
        out string matchDetails)
    {
        advertisedName = device.Name?.Trim() ?? string.Empty;
        matchDetails = string.Empty;

        bool hasServiceUuidRecords = false;
        bool hasExpectedServiceUuid = false;

        foreach (var record in device.AdvertisementRecords
                     ?? Array.Empty<AdvertisementRecord>())
        {
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

            bool is128BitUuidRecord =
                record.Type is
                    AdvertisementRecordType.UuidsIncomplete128Bit or
                    AdvertisementRecordType.UuidsComplete128Bit;

            if (!is128BitUuidRecord)
                continue;

            byte[] data = record.Data;

            for (int offset = 0;
                 offset + 16 <= data.Length;
                 offset += 16)
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

        // A matching unique service UUID is sufficient for identification.
        if (hasExpectedServiceUuid)
        {
            matchDetails =
                string.Equals(
                    advertisedName,
                    AdvertisedName,
                    StringComparison.OrdinalIgnoreCase)
                ? "Advertised name + service UUID"
                : string.IsNullOrWhiteSpace(advertisedName)
                    ? "Service UUID; local name unavailable"
                    : $"Service UUID; reported name: {advertisedName}";

            return true;
        }

        // If the advertisement contains service UUID records,
        // do not identify it by name alone when those UUIDs differ.
        if (hasServiceUuidRecords)
            return false;

        // Fall back to the exact name only when UUID records are unavailable.
        if (string.Equals(
                advertisedName,
                AdvertisedName,
                StringComparison.OrdinalIgnoreCase))
        {
            matchDetails =
                "Exact advertised name; service UUID record unavailable";

            return true;
        }

        return false;
    }
}