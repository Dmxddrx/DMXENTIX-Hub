namespace DMXENTIX.Hub.Services.Bluetooth;

/// <summary>
/// Holds the device session currently selected by the application UI.
/// Consumers receive the selected session directly instead of
/// retrieving a global active session from BluetoothService.
/// </summary>
public sealed class DeviceSessionSelectionService
{
    private IDeviceSession? _selectedSession;

    public IDeviceSession? SelectedSession => _selectedSession;

    public event Action<IDeviceSession?>? SelectedSessionChanged;

    public void SetSelectedSession(IDeviceSession? session)
    {
        if (ReferenceEquals(_selectedSession, session))
            return;

        _selectedSession = session;

        SelectedSessionChanged?.Invoke(session);
    }
}