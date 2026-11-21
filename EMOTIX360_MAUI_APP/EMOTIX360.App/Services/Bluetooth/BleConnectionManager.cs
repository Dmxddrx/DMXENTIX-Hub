using System.Diagnostics;
using EMOTIX360.App.Models.Devices;
using EMOTIX360.App.Services.Devices;
using Plugin.BLE.Abstractions.Contracts;

using AppDeviceType =
    global::EMOTIX360.App.Models.Devices.DeviceType;

namespace EMOTIX360.App.Services.Bluetooth;

/// <summary>
/// Manages independent BLE device sessions.
///
/// BleCentralService owns the shared adapter and scanner.
/// BleConnectionManager owns the collection of sessions.
/// BleDeviceSession owns each individual GATT connection.
/// </summary>
public sealed class BleConnectionManager : IAsyncDisposable
{
    private readonly BleCentralService _bleCentral;

    private readonly DeviceDiscoveryService _discovery;

    private readonly Dictionary<AppDeviceType, IDeviceProtocol> _protocols;

    private readonly Dictionary<Guid, BleDeviceSession> _sessions = new();

    private readonly Dictionary<Guid, SemaphoreSlim> _connectionLocks = new();

    private readonly object _syncLock = new();

    private bool _disposed;

    public BleConnectionManager(
        BleCentralService bleCentral,
        DeviceDiscoveryService discovery,
        IEnumerable<IDeviceProtocol> protocols)
    {
        _bleCentral = bleCentral;
        _discovery = discovery;

        _protocols = protocols.ToDictionary(
            protocol => protocol.DeviceType
        );
    }

    // ========================================================
    // HANDLE BLUETOOTH ADAPTER UNAVAILABLE
    // ========================================================

    public async Task HandleBluetoothUnavailableAsync(
        CancellationToken cancellationToken = default)
    {
        IDeviceSession[] activeSessions = Sessions
            .Where(session =>
                session.ConnectionState != DeviceConnectionState.Disconnected &&
                session.ConnectionState != DeviceConnectionState.Discovered)
            .ToArray();

        Debug.WriteLine(
            $"[BleConnectionManager] Bluetooth unavailable. " +
            $"Invalidating {activeSessions.Length} active session(s)."
        );

        foreach (IDeviceSession session in activeSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await session.HandleBluetoothUnavailableAsync(
                cancellationToken
            );
        }
    }

    // ========================================================
    // SESSION SNAPSHOTS
    // ========================================================

    public IReadOnlyList<IDeviceSession> Sessions
    {
        get
        {
            lock (_syncLock)
            {
                return _sessions.Values
                    .Cast<IDeviceSession>()
                    .ToArray();
            }
        }
    }

    public int ConnectedSessionCount =>
        Sessions.Count(session => session.IsConnected);

    public bool TryGetSession(
        Guid deviceId,
        out IDeviceSession? session)
    {
        lock (_syncLock)
        {
            if (_sessions.TryGetValue(
                    deviceId,
                    out BleDeviceSession? found))
            {
                session = found;
                return true;
            }
        }

        session = null;
        return false;
    }

    public IDeviceSession? GetSession(Guid deviceId)
    {
        TryGetSession(deviceId, out IDeviceSession? session);

        return session;
    }

    // ========================================================
    // SESSION EVENTS
    // ========================================================

    public event Action<IDeviceSession>? SessionAdded;

    public event Action<IDeviceSession>? SessionRemoved;

    public event Action<IDeviceSession>? SessionStateChanged;

    // ========================================================
    // CONNECT DISCOVERED DEVICE
    // ========================================================

    public async Task<IDeviceSession> ConnectAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DiscoveredDeviceInfo? discovered =
            _discovery.DiscoveredDevices.FirstOrDefault(
                device => device.Id == deviceId
            );

        if (discovered is null)
        {
            throw new InvalidOperationException(
                "The selected device is no longer in discovery results. Scan again."
            );
        }

        if (!_bleCentral.TryGetDiscoveredDevice(
                deviceId,
                out IDevice? device) ||
            device is null)
        {
            throw new InvalidOperationException(
                "The BLE peripheral is no longer available. Scan again."
            );
        }

        return await ConnectCoreAsync(
            deviceId,
            discovered.DeviceType,
            discovered.ProductName,
            (session, token) => session.ConnectAsync(device, token),
            cancellationToken
        );
    }

    // ========================================================
    // CONNECT REMEMBERED DEVICE
    // ========================================================

    public async Task<IDeviceSession> ConnectKnownDeviceAsync(
        Guid deviceId,
        AppDeviceType deviceType,
        string productName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await ConnectCoreAsync(
            deviceId,
            deviceType,
            productName,
            (session, token) => session.ConnectKnownDeviceAsync(token),
            cancellationToken
        );
    }

    // ========================================================
    // COMMON SESSION CONNECTION
    // ========================================================

    private async Task<IDeviceSession> ConnectCoreAsync(
        Guid deviceId,
        AppDeviceType deviceType,
        string productName,
        Func<BleDeviceSession, CancellationToken, Task> connectOperation,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim connectionLock = GetConnectionLock(deviceId);

        await connectionLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_protocols.TryGetValue(
                    deviceType,
                    out IDeviceProtocol? protocol))
            {
                throw new NotSupportedException(
                    $"No BLE protocol is registered for {deviceType}."
                );
            }

            BleDeviceSession session;
            bool added = false;

            lock (_syncLock)
            {
                if (_sessions.TryGetValue(
                        deviceId,
                        out BleDeviceSession? existing))
                {
                    if (existing.DeviceType != deviceType)
                    {
                        throw new InvalidOperationException(
                            "The existing session belongs to a different product type."
                        );
                    }

                    session = existing;
                }
                else
                {
                    session = new BleDeviceSession(
                        deviceId,
                        deviceType,
                        productName,
                        _bleCentral.Adapter,
                        protocol
                    );

                    session.StateChanged += OnSessionStateChanged;

                    _sessions.Add(deviceId, session);

                    added = true;
                }
            }

            // Notify subscribers before connecting.
            // This allows the compatibility facade to observe
            // the session before its first notification arrives.
            if (added)
            {
                SessionAdded?.Invoke(session);
            }

            await connectOperation(
                session,
                cancellationToken
            );

            return session;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    // ========================================================
    // PER-DEVICE CONNECTION LOCK
    // ========================================================

    private SemaphoreSlim GetConnectionLock(Guid deviceId)
    {
        lock (_syncLock)
        {
            if (!_connectionLocks.TryGetValue(
                    deviceId,
                    out SemaphoreSlim? connectionLock))
            {
                connectionLock = new SemaphoreSlim(1, 1);

                _connectionLocks.Add(
                    deviceId,
                    connectionLock
                );
            }

            return connectionLock;
        }
    }

    // ========================================================
    // DISCONNECT ONE DEVICE
    // ========================================================

    public async Task DisconnectAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        IDeviceSession? session = GetSession(deviceId);

        if (session is null)
            return;

        await session.DisconnectAsync(cancellationToken);
    }

    // ========================================================
    // DISCONNECT ALL DEVICES
    // ========================================================

    public async Task DisconnectAllAsync(
        CancellationToken cancellationToken = default)
    {
        IDeviceSession[] sessions = Sessions.ToArray();

        foreach (IDeviceSession session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await session.DisconnectAsync(cancellationToken);
        }
    }

    // ========================================================
    // REMOVE A SESSION
    // ========================================================

    public async Task RemoveSessionAsync(Guid deviceId)
    {
        SemaphoreSlim connectionLock = GetConnectionLock(deviceId);

        await connectionLock.WaitAsync();

        try
        {
            BleDeviceSession? session;

            lock (_syncLock)
            {
                if (!_sessions.TryGetValue(deviceId, out session))
                    return;
            }

            await session.DisposeAsync();

            lock (_syncLock)
            {
                _sessions.Remove(deviceId);
            }

            SessionRemoved?.Invoke(session);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    // ========================================================
    // FORWARD SESSION STATE CHANGES
    // ========================================================

    private void OnSessionStateChanged(IDeviceSession session)
    {
        SessionStateChanged?.Invoke(session);
    }

    // ========================================================
    // DISPOSE
    // ========================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        BleDeviceSession[] sessions;

        lock (_syncLock)
        {
            sessions = _sessions.Values.ToArray();

            _sessions.Clear();
        }

        foreach (BleDeviceSession session in sessions)
        {
            session.StateChanged -= OnSessionStateChanged;

            await session.DisposeAsync();
        }

        lock (_syncLock)
        {
            foreach (SemaphoreSlim semaphore in _connectionLocks.Values)
            {
                semaphore.Dispose();
            }

            _connectionLocks.Clear();
        }

        SessionAdded = null;
        SessionRemoved = null;
        SessionStateChanged = null;
    }
}