using System.Text.Json;
using System.Text.Json.Serialization;
using DMXENTIX.Hub.Models.Devices;
using Microsoft.Maui.Storage;

namespace DMXENTIX.Hub.Services.Storage;

/// <summary>
/// Stores the device registry as a versioned JSON document in the app's private
/// data directory. Writes use a temporary file followed by an atomic replacement
/// so an interrupted write is less likely to leave a partially written registry.
/// </summary>
public sealed class JsonDeviceRegistryStore : IDeviceRegistryStore
{
    private const int CurrentSchemaVersion = 1;
    private const string RegistryFileName = "device-registry.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _ioSemaphore = new(1, 1);

    public JsonDeviceRegistryStore()
    {
        _filePath = Path.Combine(
            FileSystem.AppDataDirectory,
            RegistryFileName
        );
    }

    public async Task<IReadOnlyList<DeviceRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _ioSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<DeviceRecord>();

            await using FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true
            );

            DeviceRegistryDocument? document =
                await JsonSerializer.DeserializeAsync<DeviceRegistryDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken
                );

            if (document is null)
            {
                throw new InvalidDataException(
                    "The device registry file is empty or invalid."
                );
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"Device registry schema version {document.SchemaVersion} " +
                    $"is not supported. Current version: {CurrentSchemaVersion}."
                );
            }

            return document.Devices ?? [];
        }
        catch (JsonException ex)
        {
            // Do not silently replace a malformed file with an empty registry.
            // Preserving the file makes recovery and diagnosis possible.
            throw new InvalidDataException(
                "The device registry contains invalid JSON. The original file was preserved.",
                ex
            );
        }
        finally
        {
            _ioSemaphore.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<DeviceRecord> devices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);

        await _ioSemaphore.WaitAsync(cancellationToken);

        string temporaryPath =
            $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The device registry directory could not be resolved."
                );
            }

            Directory.CreateDirectory(directory);

            DeviceRegistryDocument document = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                Devices = devices.ToList()
            };

            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken
                );

                await stream.FlushAsync(cancellationToken);
            }

            // The temp file is in the same directory as the destination.
            // File.Move with overwrite replaces the previous registry atomically
            // on supported app file systems.
            File.Move(
                temporaryPath,
                _filePath,
                overwrite: true
            );
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Do not hide the original storage exception with cleanup failure.
            }

            _ioSemaphore.Release();
        }
    }

    private sealed class DeviceRegistryDocument
    {
        public DeviceRegistryDocument()
        {
        }

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<DeviceRecord>? Devices { get; set; } = [];
    }
}
