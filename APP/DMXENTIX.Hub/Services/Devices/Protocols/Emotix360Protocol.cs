using System.Text;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices.Profiles;
using AppDeviceType = global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.Services.Devices.Protocols;

/// <summary>
/// BLE GATT protocol definition for EMOTIX360.
///
/// This class contains protocol metadata and payload encoding.
/// It does not own a BLE connection or characteristic.
/// </summary>
public sealed class Emotix360Protocol : IDeviceProtocol
{
    private const int MaximumProvisioningFrameLength = 20;

    public AppDeviceType DeviceType =>
        AppDeviceType.Emotix360;

    public Guid ServiceUuid =>
        Emotix360DeviceProfile.ServiceUuid;

    public Guid CommandCharacteristicUuid =>
        Guid.Parse("beb5483e-36e1-4688-b7f5-ea07361b26a8");

    public Guid? StatusCharacteristicUuid =>
        Guid.Parse("e3223119-9445-4e96-a4a1-85358c4046a2");

    public Guid? WifiProvisioningCharacteristicUuid =>
        Guid.Parse("d3600001-5a3b-4b3f-9c21-8f5a2d7c0101");

    /// <summary>
    /// EMOTIX360 commands use UTF-8 without a newline.
    /// </summary>
    public byte[] EncodeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(
                "Command cannot be empty.",
                nameof(command)
            );
        }

        return Encoding.UTF8.GetBytes(command.Trim());
    }

    /// <summary>
    /// EMOTIX360 provisioning frames use printable ASCII,
    /// with a maximum frame length of 20 bytes.
    /// </summary>
    public byte[] EncodeProvisioningFrame(string frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            throw new ArgumentException(
                "Wi-Fi provisioning frame cannot be empty.",
                nameof(frame)
            );
        }

        if (frame.Length > MaximumProvisioningFrameLength ||
            frame.Any(character => character < '!' || character > '~'))
        {
            throw new ArgumentException(
                "Invalid Wi-Fi provisioning frame. " +
                "Frames must contain 1–20 printable ASCII characters.",
                nameof(frame)
            );
        }

        return Encoding.ASCII.GetBytes(frame);
    }
}