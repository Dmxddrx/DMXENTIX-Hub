using DMXENTIX.Hub.Models.Devices;

namespace DMXENTIX.Hub.Services.Storage;

/// <summary>
/// Persistence boundary for the saved-device registry. Keeping storage behind
/// this interface allows the JSON implementation to be replaced by SQLite later.
/// </summary>
public interface IDeviceRegistryStore
{
    Task<IReadOnlyList<DeviceRecord>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyCollection<DeviceRecord> devices,
        CancellationToken cancellationToken = default);
}
