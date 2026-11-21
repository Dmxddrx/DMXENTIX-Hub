using Microsoft.Maui.ApplicationModel;

#if ANDROID
using DMXENTIX.Hub.Platforms.Android;
#endif

namespace DMXENTIX.Hub.Services;

public sealed class BluetoothPermissionService
{
    public async Task<bool> EnsurePermissionsAsync()
    {
#if ANDROID

        PermissionStatus status;

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            status =
                await Permissions.RequestAsync<BluetoothRuntimePermissions>();
        }
        else
        {
            status =
                await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        return status == PermissionStatus.Granted;

#else

        // iOS requests Bluetooth authorization through CoreBluetooth.
        // The usage description is already present in Info.plist.
        return true;

#endif
    }
}