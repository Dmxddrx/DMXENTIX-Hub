using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services.Storage;
using Microsoft.Maui.Storage;
using System.Diagnostics;
using AppDeviceType = DMXENTIX.Hub.Models.Devices.DeviceType;
using DMXENTIX.Hub.Services.Devices.Profiles;

namespace DMXENTIX.Hub.Services.Devices;

/// <summary>
/// Owns saved-device records and provides a single place to update their
/// metadata, connection timestamps, favourites and auto-reconnect preferences.
/// Runtime connection state is intentionally managed separately.
/// </summary>
public sealed class DeviceRegistryService
{
    private const string LegacyRememberedDeviceIdKey =
        "emotix360_remembered_ble_device_id";

    private const string LegacyRememberedDeviceNameKey =
        "emotix360_remembered_ble_device_name";

    private readonly IDeviceRegistryStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<DeviceRecord> _devices = [];
    private readonly DeviceProfileCatalog _profileCatalog;

    private bool _isInitialized;

    public DeviceRegistryService(
        IDeviceRegistryStore store,
        DeviceProfileCatalog profileCatalog)
    {
        _store = store;
        _profileCatalog = profileCatalog;
    }

    public event Action? DevicesChanged;

    /// <summary>
    /// Loads the registry once and imports the previously remembered EMOTIX360
    /// BLE identity if it is not already present. The old Preferences values are
    /// deliberately retained for now so the existing BluetoothService continues
    /// to reconnect exactly as it did before this migration.
    /// </summary>
    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        bool registryChanged = false;

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_isInitialized)
                return;

            IReadOnlyList<DeviceRecord> storedDevices =
                await _store.LoadAsync(cancellationToken);

            _devices.Clear();
            _devices.AddRange(storedDevices);

            ValidateLoadedRecords(_devices);

            // Synchronize saved capability declarations with
            // the currently registered product profiles.
            registryChanged = RefreshCapabilitiesFromProfiles();

            // Import the legacy EMOTIX360 record when required.
            registryChanged |= TryImportLegacyEmotix360Record();

            if (registryChanged)
            {
                await _store.SaveAsync(
                    _devices,
                    cancellationToken
                );
            }

            _isInitialized = true;

            Debug.WriteLine(
                $"[DeviceRegistry] Initialized with {_devices.Count} saved device(s). " +
                (registryChanged
                    ? "Imported the legacy EMOTIX360 identity."
                    : "No legacy migration was required.")
            );
        }
        finally
        {
            _gate.Release();
        }

        if (registryChanged)
            DevicesChanged?.Invoke();
    }

    /// <summary>
    /// Returns a snapshot sorted by most recent connection first. Devices with
    /// no recorded connection timestamp fall back to their registry-added time.
    /// </summary>
    public async Task<IReadOnlyList<DeviceRecord>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return _devices
                .OrderByDescending(device =>
                    device.LastConnectedAtUtc ?? device.AddedAtUtc)
                .ThenByDescending(device => device.AddedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceRecord?> GetDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);

        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return _devices.FirstOrDefault(device =>
                string.Equals(
                    device.DeviceId,
                    deviceId,
                    StringComparison.Ordinal
                ));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a device or refreshes its product metadata. Existing user preferences
    /// and original timestamps are retained when the record is refreshed.
    /// </summary>
    public async Task AddOrUpdateAsync(
        DeviceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);

        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            List<DeviceRecord> updatedDevices = _devices.ToList();

            int existingIndex = updatedDevices.FindIndex(device =>
                string.Equals(
                    device.DeviceId,
                    record.DeviceId,
                    StringComparison.Ordinal
                ));

            EnsureBluetoothIdentityIsUnique(
                record.BluetoothDeviceId,
                existingIndex
            );

            DeviceRecord normalizedRecord = record with
            {
                AddedAtUtc = record.AddedAtUtc == default
                    ? DateTimeOffset.UtcNow
                    : record.AddedAtUtc.ToUniversalTime(),
                LastConnectedAtUtc = record.LastConnectedAtUtc?.ToUniversalTime(),
                LastSeenAtUtc = record.LastSeenAtUtc?.ToUniversalTime()
            };

            if (existingIndex >= 0)
            {
                DeviceRecord existing = updatedDevices[existingIndex];

                if (existing.DeviceType != normalizedRecord.DeviceType)
                {
                    throw new InvalidOperationException(
                        "A saved device's product type cannot be changed for the same registry ID."
                    );
                }

                normalizedRecord = normalizedRecord with
                {
                    AddedAtUtc = existing.AddedAtUtc,
                    LastConnectedAtUtc =
                        normalizedRecord.LastConnectedAtUtc ?? existing.LastConnectedAtUtc,
                    LastSeenAtUtc =
                        normalizedRecord.LastSeenAtUtc ?? existing.LastSeenAtUtc,
                    BluetoothDeviceId =
                        normalizedRecord.BluetoothDeviceId ?? existing.BluetoothDeviceId,
                    AdvertisedName =
                        normalizedRecord.AdvertisedName ?? existing.AdvertisedName,
                    ProtocolVersion =
                        normalizedRecord.ProtocolVersion ?? existing.ProtocolVersion,
                    FirmwareVersion =
                        normalizedRecord.FirmwareVersion ?? existing.FirmwareVersion,
                    Capabilities = normalizedRecord.Capabilities == DeviceCapabilities.None
                        ? existing.Capabilities
                        : normalizedRecord.Capabilities,
                    AutoReconnectEnabled = existing.AutoReconnectEnabled,
                    IsFavorite = existing.IsFavorite
                };

                updatedDevices[existingIndex] = normalizedRecord;
            }
            else
            {
                updatedDevices.Add(normalizedRecord);
            }

            // Update the in-memory collection only after persistence succeeds.
            await _store.SaveAsync(updatedDevices, cancellationToken);

            _devices.Clear();
            _devices.AddRange(updatedDevices);
        }
        finally
        {
            _gate.Release();
        }

        DevicesChanged?.Invoke();
    }

    public Task MarkConnectedAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return UpdateDeviceAsync(
            deviceId,
            existing => existing with
            {
                LastConnectedAtUtc = now,
                LastSeenAtUtc = now
            },
            cancellationToken
        );
    }

    public Task MarkSeenAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return UpdateDeviceAsync(
            deviceId,
            existing => existing with
            {
                LastSeenAtUtc = now
            },
            cancellationToken
        );
    }

    public Task SetAutoReconnectEnabledAsync(
        string deviceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return UpdateDeviceAsync(
            deviceId,
            existing => existing with
            {
                AutoReconnectEnabled = enabled
            },
            cancellationToken
        );
    }

    public Task SetFavoriteAsync(
        string deviceId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        return UpdateDeviceAsync(
            deviceId,
            existing => existing with
            {
                IsFavorite = isFavorite
            },
            cancellationToken
        );
    }
    public Task SetCapabilitiesAsync(
        string deviceId,
        DeviceCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        return UpdateDeviceAsync(
            deviceId,
            existing => existing with
            {
                Capabilities = capabilities
            },
            cancellationToken
        );
    }
    public async Task<bool> RemoveAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);

        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            List<DeviceRecord> updatedDevices = _devices.ToList();

            int index = updatedDevices.FindIndex(device =>
                string.Equals(
                    device.DeviceId,
                    deviceId,
                    StringComparison.Ordinal
                ));

            if (index < 0)
                return false;

            updatedDevices.RemoveAt(index);
            await _store.SaveAsync(updatedDevices, cancellationToken);

            _devices.Clear();
            _devices.AddRange(updatedDevices);
        }
        finally
        {
            _gate.Release();
        }

        DevicesChanged?.Invoke();
        return true;
    }

    private async Task UpdateDeviceAsync(
        string deviceId,
        Func<DeviceRecord, DeviceRecord> update,
        CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(update);

        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            List<DeviceRecord> updatedDevices = _devices.ToList();

            int index = updatedDevices.FindIndex(device =>
                string.Equals(
                    device.DeviceId,
                    deviceId,
                    StringComparison.Ordinal
                ));

            if (index < 0)
            {
                throw new KeyNotFoundException(
                    $"Saved device '{deviceId}' was not found."
                );
            }

            updatedDevices[index] = update(updatedDevices[index]);

            // Commit the new state to disk before publishing it in memory.
            await _store.SaveAsync(updatedDevices, cancellationToken);

            _devices.Clear();
            _devices.AddRange(updatedDevices);
        }
        finally
        {
            _gate.Release();
        }

        DevicesChanged?.Invoke();
    }

    private bool TryImportLegacyEmotix360Record()
    {
        string rememberedId = Preferences.Get(
            LegacyRememberedDeviceIdKey,
            string.Empty
        );

        if (!Guid.TryParse(rememberedId, out Guid bluetoothId))
            return false;

        string normalizedBluetoothId = bluetoothId.ToString("D");

        // Avoid creating a duplicate if a record was imported previously
        // or was already registered using the same BLE transport identity.
        bool alreadyRegistered = _devices.Any(device =>
            string.Equals(
                device.BluetoothDeviceId,
                normalizedBluetoothId,
                StringComparison.OrdinalIgnoreCase
            ));

        if (alreadyRegistered)
            return false;

        string rememberedName = Preferences.Get(
            LegacyRememberedDeviceNameKey,
            BluetoothProductName
        );

        DeviceRecord legacyRecord = new()
        {
            // This is a transitional registry key. A future firmware-provided
            // stable device identity can replace it during the profile migration.
            DeviceId = $"legacy:emotix360:{normalizedBluetoothId}",
            DeviceType = AppDeviceType.Emotix360,
            DisplayName = BluetoothProductName,
            AdvertisedName = string.IsNullOrWhiteSpace(rememberedName)
                ? BluetoothProductName
                : rememberedName,
            BluetoothDeviceId = normalizedBluetoothId,
            AddedAtUtc = DateTimeOffset.UtcNow,

            // The old app did not store the actual connection timestamp.
            // Leave it null rather than inventing a historical date.
            LastConnectedAtUtc = null,
            LastSeenAtUtc = null,

            // Preserve the old app's startup auto-connect behaviour.
            AutoReconnectEnabled = true,
            IsFavorite = false,
            ProtocolVersion = 1,
            Capabilities =
                _profileCatalog.GetCapabilities(AppDeviceType.Emotix360)
        };

        _devices.Add(legacyRecord);

        return true;
    }

    private static void ValidateLoadedRecords(
        IReadOnlyCollection<DeviceRecord> devices)
    {
        HashSet<string> deviceIds = new(StringComparer.Ordinal);
        HashSet<string> bluetoothIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            ValidateRecord(device);

            if (!deviceIds.Add(device.DeviceId))
            {
                throw new InvalidDataException(
                    $"The device registry contains duplicate ID '{device.DeviceId}'."
                );
            }

            if (!string.IsNullOrWhiteSpace(device.BluetoothDeviceId) &&
                !bluetoothIds.Add(device.BluetoothDeviceId))
            {
                throw new InvalidDataException(
                    "The device registry assigns the same Bluetooth identifier to multiple devices."
                );
            }
        }
    }

    private void EnsureBluetoothIdentityIsUnique(
        string? bluetoothDeviceId,
        int existingIndex)
    {
        if (string.IsNullOrWhiteSpace(bluetoothDeviceId))
            return;

        bool usedByAnotherRecord = _devices
            .Where((_, index) => index != existingIndex)
            .Any(device => string.Equals(
                device.BluetoothDeviceId,
                bluetoothDeviceId,
                StringComparison.OrdinalIgnoreCase
            ));

        if (usedByAnotherRecord)
        {
            throw new InvalidOperationException(
                "This Bluetooth identifier is already assigned to another saved device."
            );
        }
    }

    private static void ValidateRecord(DeviceRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DeviceId))
        {
            throw new ArgumentException(
                "A saved device must have a non-empty registry ID.",
                nameof(record)
            );
        }

        if (string.IsNullOrWhiteSpace(record.DisplayName))
        {
            throw new ArgumentException(
                "A saved device must have a display name.",
                nameof(record)
            );
        }

        if (!Enum.IsDefined(record.DeviceType))
        {
            throw new ArgumentException(
                "The saved device has an unsupported product type.",
                nameof(record)
            );
        }
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException(
                "Device ID cannot be empty.",
                nameof(deviceId)
            );
        }
    }

    // ========================================================
    // REFRESH SAVED DEVICE CAPABILITIES FROM PRODUCT PROFILES
    // ========================================================

    private bool RefreshCapabilitiesFromProfiles()
    {
        bool changed = false;

        for (int index = 0; index < _devices.Count; index++)
        {
            DeviceRecord existing = _devices[index];

            // Leave records without a registered product profile unchanged.
            if (!_profileCatalog.TryGetProfile(
                    existing.DeviceType,
                    out IDeviceProfile? profile) ||
                profile is null)
            {
                continue;
            }

            DeviceCapabilities currentCapabilities =
                profile.Capabilities;

            if (existing.Capabilities == currentCapabilities)
                continue;

            // Only update capabilities.
            // Preserve custom names, favourites, reconnect settings
            // and all device identity information.
            _devices[index] = existing with
            {
                Capabilities = currentCapabilities
            };

            changed = true;
        }

        return changed;
    }

    private const string BluetoothProductName = "EMOTIX360";
}
