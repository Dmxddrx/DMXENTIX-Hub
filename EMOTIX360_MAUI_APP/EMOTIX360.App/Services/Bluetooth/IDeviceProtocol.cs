using EMOTIX360.App.Models.Devices;

using AppDeviceType = EMOTIX360.App.Models.Devices.DeviceType;

namespace EMOTIX360.App.Services.Bluetooth;

/// <summary>
/// Describes the BLE GATT protocol used by a product.
///
/// Implementations should be stateless. Device-specific GATT
/// objects and runtime state belong to BleDeviceSession.
/// </summary>
public interface IDeviceProtocol
{
    AppDeviceType DeviceType { get; }

    Guid ServiceUuid { get; }

    Guid CommandCharacteristicUuid { get; }

    Guid? StatusCharacteristicUuid { get; }

    Guid? WifiProvisioningCharacteristicUuid { get; }

    /// <summary>
    /// Converts a product command into its BLE payload.
    /// </summary>
    byte[] EncodeCommand(string command);

    /// <summary>
    /// Converts a product provisioning frame into its BLE payload.
    /// </summary>
    byte[] EncodeProvisioningFrame(string frame);
}