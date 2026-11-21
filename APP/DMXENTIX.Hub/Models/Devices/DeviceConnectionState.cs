namespace DMXENTIX.Hub.Models.Devices;

/// <summary>
/// Runtime connection state for a device. This is deliberately not persisted:
/// connection state must be confirmed by the live connection manager each launch.
/// </summary>
public enum DeviceConnectionState
{
    Unknown = 0,
    Disconnected = 1,
    Discovered = 2,
    Connecting = 3,
    Connected = 4,
    Reconnecting = 5,
    Error = 6
}
