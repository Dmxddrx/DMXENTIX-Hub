using System;
using System.Collections.Generic;
using System.Linq;
using DMXENTIX.Hub.Models.Devices;

using AppDeviceType =
    global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.Services.Devices.Profiles;

/// <summary>
/// Central catalogue of registered product profiles.
/// Provides one source of truth for product identity
/// and declared capabilities.
/// </summary>
public sealed class DeviceProfileCatalog
{
    private readonly IReadOnlyList<IDeviceProfile> _profiles;

    private readonly IReadOnlyDictionary<AppDeviceType, IDeviceProfile>
        _profilesByType;

    public DeviceProfileCatalog(
        IEnumerable<IDeviceProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        IDeviceProfile[] registeredProfiles =
            profiles.ToArray();

        // Each product type must have exactly one profile.
        IGrouping<AppDeviceType, IDeviceProfile>? duplicate =
            registeredProfiles
                .GroupBy(profile => profile.DeviceType)
                .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple device profiles are registered for " +
                $"DeviceType '{duplicate.Key}'."
            );
        }

        foreach (IDeviceProfile profile in registeredProfiles)
        {
            if (profile.DeviceType == AppDeviceType.Unknown)
            {
                throw new InvalidOperationException(
                    "A device profile cannot use DeviceType.Unknown."
                );
            }

            if (string.IsNullOrWhiteSpace(profile.ProductName))
            {
                throw new InvalidOperationException(
                    $"The profile for '{profile.DeviceType}' " +
                    "must provide a product name."
                );
            }
        }

        _profiles = Array.AsReadOnly(registeredProfiles);

        _profilesByType = registeredProfiles.ToDictionary(
            profile => profile.DeviceType
        );
    }

    public IReadOnlyList<IDeviceProfile> Profiles =>
        _profiles;

    public bool TryGetProfile(
        AppDeviceType deviceType,
        out IDeviceProfile? profile)
    {
        return _profilesByType.TryGetValue(
            deviceType,
            out profile
        );
    }

    public DeviceCapabilities GetCapabilities(
        AppDeviceType deviceType)
    {
        return _profilesByType.TryGetValue(
            deviceType,
            out IDeviceProfile? profile)
                ? profile.Capabilities
                : DeviceCapabilities.None;
    }
}