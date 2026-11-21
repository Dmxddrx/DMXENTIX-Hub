using EMOTIX360.App.Models.Devices;
using EMOTIX360.App.Services.Devices;
using Plugin.BLE.Abstractions.Contracts;

namespace EMOTIX360.App.Services.Bluetooth;

/// <summary>
/// Manages independent BLE device sessions.
///
/// BleCentralService owns the shared adapter and scanner.
/// BleConnectionManager owns the collection of device sessions.
/// BleDeviceSession owns each individual GATT connection.
/// </summary>
public sealed class BleConnectionManager : IAsyncDisposable
{
    private readonly BleCentralService _bleCentral;

    private readonly DeviceDiscoveryService _discovery;

    private readonly Dictionary<DeviceType, IDeviceProtocol> _protocols;

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
            if (_sessions.TryGetValue(deviceId, out BleDeviceSession? found))
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
    // CONNECT
    // ========================================================

    public async Task<IDeviceSession> ConnectAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Prevent two simultaneous connection attempts to the same device.
        // Different device IDs receive different semaphores.
        SemaphoreSlim connectionLock;

        lock (_syncLock)
        {
            if (!_connectionLocks.TryGetValue(
                    deviceId,
                    out connectionLock!))
            {
                connectionLock = new SemaphoreSlim(1, 1);

                _connectionLocks.Add(
                    deviceId,
                    connectionLock
                );
            }
        }

        await connectionLock.WaitAsync(cancellationToken);

        try
        {
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

            if (!_protocols.TryGetValue(
                    discovered.DeviceType,
                    out IDeviceProtocol? protocol))
            {
                throw new NotSupportedException(
                    $"No BLE protocol is registered for {discovered.DeviceType}."
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
                    session = existing;
                }
                else
                {
                    session = new BleDeviceSession(
                        deviceId,
                        discovered.DeviceType,
                        discovered.ProductName,
                        _bleCentral.Adapter,
                        protocol
                    );

                    session.StateChanged += OnSessionStateChanged;

                    _sessions.Add(deviceId, session);

                    added = true;
                }
            }

            if (added)
            {
                SessionAdded?.Invoke(session);
            }

            await session.ConnectAsync(
                device,
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