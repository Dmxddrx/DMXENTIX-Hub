namespace EMOTIX360.App.Models.Devices;

/// <summary>
/// Persistent information about a device the user has successfully added.
///
/// DeviceId is the app's registry key. Prefer a stable identifier supplied by
/// device firmware when available. For the current EMOTIX360 migration, a
/// legacy BLE identifier is used until firmware exposes a stable product ID.
///
/// BluetoothDeviceId is a transport identifier used to reconnect through the
/// current BLE library. It is kept separate from DeviceId so the transport
/// implementation can change without changing the app's registry identity.
/// </summary>
public sealed record DeviceRecord
{
    public required string DeviceId { get; init; }

    public required DeviceType DeviceType { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>BLE advertised name last associated with this record.</summary>
    public string? AdvertisedName { get; init; }

    /// <summary>
    /// Current Plugin.BLE device ID, stored as text so the registry does not
    /// depend on Plugin.BLE types. This may be replaced by another transport ID later.
    /// </summary>
    public string? BluetoothDeviceId { get; init; }

    public DateTimeOffset AddedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastConnectedAtUtc { get; init; }

    public DateTimeOffset? LastSeenAtUtc { get; init; }

    public bool AutoReconnectEnabled { get; init; }

    public bool IsFavorite { get; init; }

    public int? ProtocolVersion { get; init; }

    public string? FirmwareVersion { get; init; }

    public DeviceCapabilities Capabilities { get; init; }
        = DeviceCapabilities.None;
}
