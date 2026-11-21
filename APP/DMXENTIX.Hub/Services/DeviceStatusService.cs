using DMXENTIX.Hub.Models;
using DMXENTIX.Hub.Services.Bluetooth;
using System.Diagnostics;

namespace DMXENTIX.Hub.Services;

/// <summary>
/// Subscribes to one explicitly selected BLE device session
/// and exposes parsed device status snapshots.
/// </summary>
public sealed class DeviceStatusService : IDisposable
{
    private IDeviceSession? _session;

    public DeviceStatusService()
    {
    }

    /// <summary>
    /// Selects the device session whose status should be monitored.
    /// Previous session subscriptions are removed.
    /// </summary>
    public void SetSession(IDeviceSession? session)
    {
        if (ReferenceEquals(_session, session))
            return;

        // Remove subscriptions from the previous session.
        if (_session is not null)
        {
            _session.StatusPayloadReceived -=
                OnStatusPayloadReceived;

            _session.StateChanged -=
                OnSessionStateChanged;
        }

        _session = session;

        // Clear the previous device's status snapshot.
        Current = null;
        StatusCleared?.Invoke();

        if (_session is null)
            return;

        // Subscribe directly to the selected device session.
        _session.StatusPayloadReceived +=
            OnStatusPayloadReceived;

        _session.StateChanged +=
            OnSessionStateChanged;

        // Restore the latest status when selecting an
        // already-connected session.
        if (_session.IsConnected &&
            !string.IsNullOrWhiteSpace(_session.LastStatusPayload))
        {
            ProcessStatusPayload(_session.LastStatusPayload);
        }
    }

    public DeviceStatus? Current { get; private set; }

    public event Action<DeviceStatus>? StatusUpdated;

    public event Action? StatusCleared;

    public event Action<string>? UnparsedPayloadReceived;

    // ========================================================
    // SESSION STATUS NOTIFICATIONS
    // ========================================================

    private void OnStatusPayloadReceived(
        IDeviceSession session,
        string payload)
    {
        // Ignore notifications from sessions that are no longer selected.
        if (!ReferenceEquals(session, _session))
            return;

        ProcessStatusPayload(payload);
    }

    // ========================================================
    // SESSION CONNECTION STATE
    // ========================================================

    private void OnSessionStateChanged(IDeviceSession session)
    {
        if (!ReferenceEquals(session, _session))
            return;

        if (!session.IsConnected)
        {
            Current = null;
            StatusCleared?.Invoke();

            return;
        }

        // Restore the latest available status after reconnection.
        if (!string.IsNullOrWhiteSpace(session.LastStatusPayload))
        {
            ProcessStatusPayload(session.LastStatusPayload);
        }
    }

    // ========================================================
    // PARSE DEVICE STATUS
    // ========================================================

    private void ProcessStatusPayload(string payload)
    {
        Debug.WriteLine(
            $"[EMOTIX360 BLE] Status received: {payload}"
        );

        if (!DeviceStatus.TryParse(payload, out DeviceStatus status))
        {
            Debug.WriteLine(
                "[EMOTIX360 BLE] Incomplete or invalid status packet. " +
                "Previous valid snapshot retained."
            );

            UnparsedPayloadReceived?.Invoke(payload);

            return;
        }

        Current = status;

        Debug.WriteLine(
            $"[EMOTIX360 BLE] Parsed successfully: " +
            $"MODE={status.Mode}, " +
            $"BRIGHT={status.Brightness}, " +
            $"WIFI={status.WifiConnected}, " +
            $"TIME={status.TimeValid}, " +
            $"PITCH={status.PitchDegrees}, " +
            $"ROLL={status.RollDegrees}"
        );

        StatusUpdated?.Invoke(status);
    }

    // ========================================================
    // DISPOSE
    // ========================================================

    public void Dispose()
    {
        if (_session is not null)
        {
            _session.StatusPayloadReceived -=
                OnStatusPayloadReceived;

            _session.StateChanged -=
                OnSessionStateChanged;

            _session = null;
        }
    }
}