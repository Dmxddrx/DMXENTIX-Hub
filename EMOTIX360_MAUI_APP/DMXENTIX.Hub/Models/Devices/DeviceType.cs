namespace EMOTIX360.App.Models.Devices;

/// <summary>
/// Identifies the product family, not an individual physical device.
/// Keep these values stable because they are persisted in the device registry.
/// </summary>
public enum DeviceType
{
    Unknown = 0,
    Emotix360 = 1,
    ClimateNode360 = 2,
    Moppy = 3
}
