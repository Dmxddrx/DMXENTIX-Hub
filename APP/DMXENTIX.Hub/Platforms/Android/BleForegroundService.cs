
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
using DMXENTIX.Hub.Services.Bluetooth;

namespace DMXENTIX.Hub.Platforms.Android;

// Starts and stops the Android foreground service.
internal static class BleForegroundServiceController
{
    private static readonly object SyncLock = new();
    private static bool _isStarted;

    public static bool IsStarted
    {
        get
        {
            lock (SyncLock)
            {
                return _isStarted;
            }
        }
    }

    internal static void RefreshNotification()
    {
        BleForegroundService.RefreshCurrentNotification();
    }

    internal static void NotifyServiceStarted()
    {
        lock (SyncLock)
        {
            _isStarted = true;
        }
    }

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
        "dmxentix_hub_ble_connection";

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
        BleForegroundServiceController.NotifyServiceStarted();

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

        // Android can recreate the sticky foreground service after
        // terminating the app process. Restore all eligible saved devices.
        if (intent is null)
        {
            try
            {
                DeviceConnectionSupervisor? supervisor =
                    IPlatformApplication.Current?
                        .Services.GetService<DeviceConnectionSupervisor>();

                supervisor?.ResumeFromForegroundService();

                Debug.WriteLine(
                    "[BLE] Multi-device connection supervisor restored."
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
            "DMXENTIX Hub BLE Connection",
            NotificationImportance.Low
        )
        {
            Description =
                "Maintains connections and automatic reconnection for saved DMXENTIX devices."
        };

        manager.CreateNotificationChannel(channel);
    }

    private static WeakReference<BleForegroundService>?
        _currentInstance;

    public override void OnCreate()
    {
        base.OnCreate();

        _currentInstance = new WeakReference<BleForegroundService>(
            this
        );
    }

    internal static void RefreshCurrentNotification()
    {
        if (_currentInstance is not null &&
            _currentInstance.TryGetTarget(
                out BleForegroundService? service))
        {
            service.RefreshNotification();
        }
    }

    private void RefreshNotification()
    {
        NotificationManager? manager =
            GetSystemService(NotificationService) as NotificationManager;

        manager?.Notify(
            NOTIFICATION_ID,
            BuildNotification()
        );
    }

    private Notification BuildNotification()
    {
        IServiceProvider? services =
            IPlatformApplication.Current?.Services;

        BleConnectionManager? connectionManager =
            services?.GetService<BleConnectionManager>();

        DeviceConnectionSupervisor? supervisor =
            services?.GetService<DeviceConnectionSupervisor>();

        BleCentralService? bleCentral =
            services?.GetService<BleCentralService>();

        int connectedCount =
            connectionManager?.ConnectedSessionCount ?? 0;

        int reconnectingCount =
            supervisor?.ActiveReconnectCount ?? 0;

        int autoReconnectCount =
            supervisor?.AutoReconnectDeviceCount ?? 0;

        bool bluetoothOn =
            bleCentral?.IsBluetoothOn ?? true;

        string statusText;

        if (!bluetoothOn && autoReconnectCount > 0)
        {
            statusText =
                $"Bluetooth off. Waiting for {autoReconnectCount} saved device(s).";
        }
        else if (connectedCount > 0 && reconnectingCount > 0)
        {
            statusText =
                $"{connectedCount} connected · {reconnectingCount} reconnecting";
        }
        else if (reconnectingCount > 0)
        {
            statusText =
                $"Reconnecting {reconnectingCount} saved device(s).";
        }
        else if (connectedCount > 0)
        {
            statusText =
                $"Monitoring {connectedCount} active BLE connection(s).";
        }
        else if (autoReconnectCount > 0)
        {
            statusText =
                $"Monitoring {autoReconnectCount} saved device(s).";
        }
        else
        {
            statusText =
                "Bluetooth connection management is active.";
        }

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
            .SetContentTitle("DMXENTIX Hub")
            .SetContentText(statusText)
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
        if (_currentInstance is not null &&
            _currentInstance.TryGetTarget(
                out BleForegroundService? current) &&
            ReferenceEquals(current, this))
        {
            _currentInstance = null;
        }

        BleForegroundServiceController.NotifyServiceStopped();

        base.OnDestroy();
    }
}
#endif
