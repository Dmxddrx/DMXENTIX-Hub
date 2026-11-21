using EMOTIX360.App.Models.Devices;
using Plugin.BLE.Abstractions.Contracts;

namespace EMOTIX360.App.Services.Bluetooth;

/// <summary>
/// Represents one independent connection to one physical BLE device.
///
/// A session owns its device-specific GATT state and data.
/// No connection, command or notification state is shared with
/// another session.
/// </summary>
public interface IDeviceSession : IAsyncDisposable
{
    Guid DeviceId { get; }

    DeviceType DeviceType { get; }

    string ProductName { get; }

    string DeviceName { get; }

    DeviceConnectionState ConnectionState { get; }

    bool IsConnected { get; }

    /// <summary>
    /// Most recently received raw device status payload for this session.
    /// </summary>
    string? LastStatusPayload { get; }

    /// <summary>
    /// Most recently received raw Wi-Fi provisioning payload for this session.
    /// </summary>
    string? LastProvisioningStatusPayload { get; }

    event Action<IDeviceSession>? StateChanged;

    event Action<IDeviceSession, string>? StatusPayloadReceived;

    event Action<IDeviceSession, string>? ProvisioningStatusPayloadReceived;

    /// <summary>
    /// Connects this session to the supplied BLE peripheral.
    /// </summary>
    Task ConnectAsync(
        IDevice device,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Disconnects only this session's physical BLE peripheral.
    /// </summary>
    Task DisconnectAsync(
        CancellationToken cancellationToken = default
    );

    Task WriteCommandAsync(
        string command,
        CancellationToken cancellationToken = default
    );

    Task WriteWifiProvisioningFrameAsync(
        string frame,
        CancellationToken cancellationToken = default
    );
}