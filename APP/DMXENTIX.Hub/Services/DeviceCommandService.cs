
using DMXENTIX.Hub.Models;
using DMXENTIX.Hub.Services.Bluetooth;

namespace DMXENTIX.Hub.Services;

/// <summary>
/// Validates and formats the text protocol expected by the EMOTIX360 firmware.
/// These are the documented/proposed wire commands; firmware must consume takeCommand()
/// before the physical animation or brightness can change.
///
/// Commands are sent through an explicitly selected device session.
/// This service does not depend on a global active Bluetooth connection.
/// </summary>
public sealed class DeviceCommandService
{
    /// <summary>
    /// Sends an animation mode command to the specified device session.
    /// </summary>
    public Task SetModeAsync(
        IDeviceSession session,
        AnimationMode mode)
    {
        ArgumentNullException.ThrowIfNull(session);

        string command = $"MODE={mode.ToFirmwareValue()}";

        return session.WriteCommandAsync(command);
    }

    /// <summary>
    /// Sends a brightness command to the specified device session.
    /// </summary>
    public Task SetBrightnessAsync(
        IDeviceSession session,
        int brightness)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (brightness is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(brightness),
                "MAX7219 brightness must be between 0 and 15."
            );
        }

        return session.WriteCommandAsync($"BRIGHT={brightness}");
    }
}
