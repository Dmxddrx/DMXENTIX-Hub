
#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using DMXENTIX.Hub.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using System.Runtime.Versioning;
using Debug = System.Diagnostics.Debug;

namespace DMXENTIX.Hub.Platforms.Android;

// Starts and stops the Android foreground service.
internal static class BleForegroundServiceController
{
    private static readonly object SyncLock = new();
    private static bool _isStarted;

    public static void Start()
    {
        lock (SyncLock)
        {
            if (_isStarted)
                return;

            _isStarted = true;
        }

        try
        {
            Context context =
                global::Android.App.Application.Context;

            Intent intent = new Intent(
                context,
                typeof(BleForegroundService)
            );

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            Debug.WriteLine(
                "[BLE] Android foreground service requested."
            );
        }
        catch (Exception ex)
        {
            NotifyServiceStopped();

            Debug.WriteLine(
                $"[BLE] Foreground service start failed: {ex.Message}"
            );
        }
    }

    public static void Stop()
    {
        NotifyServiceStopped();

        try
        {
            Context context =
                global::Android.App.Application.Context;

            Intent intent = new Intent(
                context,
                typeof(BleForegroundService)
            );

            context.StopService(intent);

            Debug.WriteLine(
                "[BLE] Android foreground service stopped."
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Foreground service stop failed: {ex.Message}"
            );
        }
    }

    internal static void NotifyServiceStopped()
    {
        lock (SyncLock)
        {
            _isStarted = false;
        }
    }
}

[Service(
    Name = "com.dmxddrx.emotix360.BleForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeConnectedDevice
)]
public sealed class BleForegroundService : Service
{
    private const string CHANNEL_ID =
        "emotix360_ble_connection";

    private const int NOTIFICATION_ID = 3601;

    public override IBinder? OnBind(Intent? intent)
    {
        return null;
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            CreateNotificationChannel();
        }

        Notification notification = BuildNotification();

        // Android 10+ supports specifying the foreground-service type
        // directly when promoting the service.
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(
                NOTIFICATION_ID,
                notification,
                ForegroundService.TypeConnectedDevice
            );
        }
        else
        {
            StartForeground(
                NOTIFICATION_ID,
                notification
            );
        }

        // Android can recreate a sticky foreground service after
        // the app process has been terminated by the system.
        // In that case, Android supplies a null start Intent.
        //
        // Restore the existing BLE auto-connect manager using
        // the MAUI dependency injection container.
        if (intent is null)
        {
            try
            {
                BluetoothService? bluetoothService =
                    IPlatformApplication.Current?
                        .Services.GetService<BluetoothService>();

                bluetoothService?
                    .ResumeAutoConnectFromForegroundService();

                Debug.WriteLine(
                    "[BLE] Auto-connect manager restored by foreground service."
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[BLE] Foreground service recovery failed: {ex.Message}"
                );
            }
        }

        return StartCommandResult.Sticky;
    }

    [SupportedOSPlatform("android26.0")]
    private void CreateNotificationChannel()
    {
        NotificationManager? manager =
            GetSystemService(NotificationService) as NotificationManager;

        if (manager is null ||
            manager.GetNotificationChannel(CHANNEL_ID) is not null)
        {
            return;
        }

        NotificationChannel channel = new NotificationChannel(
            CHANNEL_ID,
            "EMOTIX360 BLE Connection",
            NotificationImportance.Low
        )
        {
            Description =
                "Maintains the Bluetooth connection and automatic reconnection."
        };

        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        Intent launchIntent = new Intent(
            this,
            typeof(global::DMXENTIX.Hub.MainActivity)
        );

        launchIntent.AddFlags(
            ActivityFlags.SingleTop |
            ActivityFlags.ClearTop
        );

        PendingIntent? pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent |
            PendingIntentFlags.Immutable
        );

        Notification.Builder builder;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            builder = new Notification.Builder(
                this,
                CHANNEL_ID
            );
        }
        else
        {
            builder = new Notification.Builder(this);
        }

        return builder
            .SetContentTitle("EMOTIX360")
            .SetContentText(
                "Bluetooth connection monitoring is active."
            )
            .SetSmallIcon(
                global::Android.Resource.Drawable.IcDialogInfo
            )
            .SetContentIntent(pendingIntent)
            .SetCategory(Notification.CategoryService)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .Build();
    }

    public override void OnDestroy()
    {
        BleForegroundServiceController.NotifyServiceStopped();

        base.OnDestroy();
    }
}
#endif
