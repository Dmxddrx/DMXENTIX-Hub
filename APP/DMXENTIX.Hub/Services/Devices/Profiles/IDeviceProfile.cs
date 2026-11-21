using DMXENTIX.Hub.Models.Devices;
using Plugin.BLE.Abstractions.Contracts;

using AppDeviceType = DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.Services.Devices.Profiles;

/// <summary>
/// Defines how a supported product is identified from BLE advertisements.
/// Each product registers its own profile.
/// </summary>
public interface IDeviceProfile
{
    AppDeviceType DeviceType { get; }

    string ProductName { get; }

    DeviceCapabilities Capabilities { get; }

    bool TryMatch(
        IDevice device,
        out string advertisedName,
        out string matchDetails
    );
}