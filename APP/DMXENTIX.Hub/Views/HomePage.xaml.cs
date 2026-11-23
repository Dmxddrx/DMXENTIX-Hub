using DMXENTIX.Hub.ViewModels;
using Microsoft.Maui.ApplicationModel;
using System.Diagnostics;

#if ANDROID
using DMXENTIX.Hub.Platforms.Android;
#endif

namespace DMXENTIX.Hub.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

#if ANDROID
    private bool _notificationPermissionRequested;
#endif

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;

        BindingContext = _viewModel;
    }

    // ========================================================
    // REFRESH HOME WHEN IT BECOMES VISIBLE
    // ========================================================

    protected override async void OnAppearing()
    {
        base.OnAppearing();

#if ANDROID
        await RequestNotificationPermissionAsync();
#endif

        await _viewModel.InitializeAsync();
    }

    // ========================================================
    // FIND DEVICE
    // ========================================================

    private async void OnFindDeviceClicked(
        object sender,
        EventArgs e)
    {
        Shell? shell = Shell.Current;

        if (shell is null)
            return;

        await shell.GoToAsync("devices");
    }

    // ========================================================
    // CLOSE INFORMATION POPUP FROM ANDROID BACK
    // ========================================================

    public bool TryCloseInfoPopup()
    {
        if (!_viewModel.IsInfoPopupVisible)
            return false;

        _viewModel.CloseInformationPopup();

        return true;
    }

#if ANDROID
    private async Task RequestNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) ||
            _notificationPermissionRequested)
        {
            return;
        }

        _notificationPermissionRequested = true;

        try
        {
            PermissionStatus status =
                await Permissions.CheckStatusAsync<
                    Permissions.PostNotifications>();

            if (status != PermissionStatus.Granted)
            {
                status =
                    await Permissions.RequestAsync<
                        Permissions.PostNotifications>();
            }

            if (status == PermissionStatus.Granted)
            {
                // Refresh the existing foreground notification after
                // the user grants permission.
                BleForegroundServiceController.RefreshNotification();
            }
            else
            {
                Debug.WriteLine(
                    "[BLE] Notification permission was not granted. " +
                    "Foreground service operation can continue."
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Notification permission request failed: {ex.Message}"
            );
        }
    }
#endif

}