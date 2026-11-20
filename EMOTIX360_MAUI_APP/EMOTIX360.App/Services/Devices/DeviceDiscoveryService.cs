using EMOTIX360.App.Models.Devices;
using EMOTIX360.App.Services.Devices.Profiles;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;

namespace EMOTIX360.App.Services.Devices;

/// <summary>
/// Identifies supported product profiles from raw BLE discovery events.
/// Maintains temporary discovery results separately from saved devices.
/// </summary>
public sealed class DeviceDiscoveryService
{
    private readonly BleCentralService _bleCentral;

    private readonly IReadOnlyList<IDeviceProfile> _profiles;

    private readonly object _devicesLock = new();

    private readonly Dictionary<Guid, DiscoveredDeviceInfo>
        _discoveredDevices = new();

    public DeviceDiscoveryService(
        BleCentralService bleCentral,
        IEnumerable<IDeviceProfile> profiles)
    {
        _bleCentral = bleCentral;

        _profiles = profiles.ToArray();

        // Log the product profiles registered through dependency injection.
        foreach (IDeviceProfile profile in _profiles)
        {
            Debug.WriteLine(
                $"[DeviceDiscovery] Registered profile: " +
                $"{profile.ProductName} ({profile.DeviceType})"
            );
        }

        // Listen to raw BLE discovery events.
        _bleCentral.DeviceDiscovered += OnBleDeviceDiscovered;

        // Maintain discovery results for each scan.
        _bleCentral.ScanStarting += OnScanStarting;
        _bleCentral.ScanCompleted += OnScanCompleted;
    }

    public event Action<DiscoveredDeviceInfo>? DeviceDiscovered;

    public event Action? DevicesChanged;

    /// <summary>
    /// Returns a snapshot of currently discovered supported products.
    /// Stronger RSSI values appear first.
    /// </summary>
    public IReadOnlyList<DiscoveredDeviceInfo> DiscoveredDevices
    {
        get
        {
            lock (_devicesLock)
            {
                return _discoveredDevices.Values
                    .OrderByDescending(device => device.Rssi)
                    .ToArray();
            }
        }
    }

    private void OnScanStarting()
    {
        lock (_devicesLock)
        {
            _discoveredDevices.Clear();
        }

        DevicesChanged?.Invoke();

        Debug.WriteLine(
            $"[DeviceDiscovery] Scan started. " +
            $"{_profiles.Count} product profile(s) registered."
        );
    }

    private void OnScanCompleted()
    {
        int count = DiscoveredDevices.Count;

        Debug.WriteLine(
            $"[DeviceDiscovery] Scan completed. " +
            $"{count} supported device(s) identified."
        );
    }

    private void OnBleDeviceDiscovered(
        object? sender,
        DeviceEventArgs args)
    {
        IDevice device = args.Device;

        // Check the advertisement against registered product profiles.
        foreach (IDeviceProfile profile in _profiles)
        {
            if (!profile.TryMatch(
                    device,
                    out string advertisedName,
                    out string matchDetails))
            {
                continue;
            }

            var discoveredDevice = new DiscoveredDeviceInfo(
                Id: device.Id,
                DeviceType: profile.DeviceType,
                ProductName: profile.ProductName,
                AdvertisedName: advertisedName,
                Rssi: device.Rssi,
                Capabilities: profile.Capabilities,
                MatchDetails: matchDetails
            );

            bool isNewDevice;

            lock (_devicesLock)
            {
                isNewDevice =
                    !_discoveredDevices.ContainsKey(device.Id);

                _discoveredDevices[device.Id] = discoveredDevice;
            }

            Debug.WriteLine(
                $"[DeviceDiscovery] " +
                $"{(isNewDevice ? "Identified" : "Updated")} " +
                $"{discoveredDevice.ProductName} | " +
                $"ID={discoveredDevice.Id} | " +
                $"Name={discoveredDevice.AdvertisedName} | " +
                $"RSSI={discoveredDevice.Rssi} dBm | " +
                $"Match={discoveredDevice.MatchDetails}"
            );

            DeviceDiscovered?.Invoke(discoveredDevice);

            DevicesChanged?.Invoke();

            // One advertisement should resolve to one product profile.
            return;
        }
    }
}