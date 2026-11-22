using System.Text;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices.Profiles;

using AppDeviceType =
    global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.Services.Devices.Protocols;

/// <summary>
/// BLE GATT protocol definition for DMXENTIX SoundNode.
/// </summary>
public sealed class SoundNodeProtocol : IDeviceProtocol
{
    public AppDeviceType DeviceType =>
        AppDeviceType.SoundNode;

    public Guid ServiceUuid =>
        SoundNodeDeviceProfile.ServiceUuid;

    public Guid CommandCharacteristicUuid =>
        Guid.Parse("d3601001-5a3b-4b3f-9c21-8f5a2d7c0101");

    public Guid? StatusCharacteristicUuid =>
        Guid.Parse("d3601002-5a3b-4b3f-9c21-8f5a2d7c0101");

    public Guid? WifiProvisioningCharacteristicUuid =>
        null;

    /// <summary>
    /// SoundNode commands use UTF-8 without a newline.
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
    /// SoundNode does not support Wi-Fi provisioning.
    /// </summary>
    public byte[] EncodeProvisioningFrame(string frame)
    {
        throw new NotSupportedException(
            "SoundNode does not support Wi-Fi provisioning."
        );
    }
}