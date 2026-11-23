using System.Collections.Generic;
using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class DeviceWorkspacePage : ContentPage, IQueryAttributable
{
    private readonly DeviceWorkspaceViewModel _viewModel;
    private readonly WifiProvisioningViewModel _wifiProvisioningViewModel;

    private string? _deviceId;

    private bool _isWifiConfigurationVisible;
    private bool _isInfoPopupVisible;

    public DeviceWorkspacePage(
        DeviceWorkspaceViewModel viewModel,
        Emotix360DashboardViewModel dashboardViewModel,
        WifiProvisioningViewModel wifiProvisioningViewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _wifiProvisioningViewModel = wifiProvisioningViewModel;

        BindingContext = _viewModel;

        // The workspace dashboard uses its dedicated EMOTIX360 ViewModel.
        Emotix360Dashboard.BindingContext = dashboardViewModel;

        // The workspace already displays its own connection information.
        Emotix360Dashboard.ShowDeviceConnectionCard = false;

        // Connect the dashboard Wi-Fi button to the workspace screen.
        Emotix360Dashboard.WifiConfigurationRequested +=
            OnWifiConfigurationRequested;

        // Wi-Fi provisioning has a separate workspace-specific ViewModel.
        Emotix360WifiProvisioning.BindingContext =
            _wifiProvisioningViewModel;
    }

    // ========================================================
    // RECEIVE THE PERSISTENT DEVICE ID FROM SHELL NAVIGATION
    // ========================================================

    public void ApplyQueryAttributes(
        IDictionary<string, object> query)
    {
        if (query.TryGetValue("DeviceId", out object? value) &&
            value is string deviceId)
        {
            _deviceId = deviceId;
        }
        else
        {
            _deviceId = null;
        }
    }

    // ========================================================
    // OPEN WI-FI CONFIGURATION
    // ========================================================

    private void OnWifiConfigurationRequested(
        object? sender,
        EventArgs e)
    {
        if (!_viewModel.IsEmotix360Workspace ||
            !_viewModel.IsConnected)
        {
            return;
        }

        _isWifiConfigurationVisible = true;

        WorkspaceContent.IsVisible = false;
        WifiConfigurationScreen.IsVisible = true;
    }

    // ========================================================
    // CLOSE WI-FI CONFIGURATION
    // ========================================================

    private void OnCloseWifiConfigurationClicked(
        object? sender,
        EventArgs e)
    {
        CloseWifiConfigurationScreen();
    }

    private void CloseWifiConfigurationScreen()
    {
        _isWifiConfigurationVisible = false;

        WifiConfigurationScreen.IsVisible = false;
        WorkspaceContent.IsVisible = true;
    }

    // Allow AppShell to close this screen before
    // handling the normal Android Back navigation.
    public bool TryCloseWifiConfiguration()
    {
        if (!_isWifiConfigurationVisible)
            return false;

        CloseWifiConfigurationScreen();

        return true;
    }

    // ========================================================
    // OPEN DEVICE INFORMATION POPUP
    // ========================================================

    private void OnShowInfoClicked(
        object? sender,
        EventArgs e)
    {
        if (!_viewModel.HasRecord)
            return;

        _isInfoPopupVisible = true;

        DeviceInfoPopup.IsVisible = true;
    }

    // ========================================================
    // CLOSE DEVICE INFORMATION POPUP
    // ========================================================

    private void OnCloseInfoPopupClicked(
        object? sender,
        EventArgs e)
    {
        CloseInfoPopup();
    }

    private void OnInfoPopupBackgroundTapped(
        object? sender,
        TappedEventArgs e)
    {
        CloseInfoPopup();
    }

    private void CloseInfoPopup()
    {
        _isInfoPopupVisible = false;

        DeviceInfoPopup.IsVisible = false;
    }

    // Allow AppShell to close the popup before
    // handling normal Android Back navigation.

    public bool TryCloseInfoPopup()
    {
        if (!_isInfoPopupVisible)
            return false;

        CloseInfoPopup();

        return true;
    }

    // ========================================================
    // LOAD WORKSPACE
    // ========================================================

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Bind the provisioning interface to this workspace's session.
        _viewModel.WorkspaceSessionChanged -=
            _wifiProvisioningViewModel.SetSession;

        _viewModel.WorkspaceSessionChanged +=
            _wifiProvisioningViewModel.SetSession;

        _wifiProvisioningViewModel.SetSession(null);

        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            await _viewModel.LoadDeviceAsync(_deviceId);
        }
    }

    // ========================================================
    // RELEASE SESSION EVENT SUBSCRIPTION
    // ========================================================

    protected override void OnDisappearing()
    {
        CloseInfoPopup();

        CloseWifiConfigurationScreen();

        // Remove the workspace-to-provisioning connection.
        _viewModel.WorkspaceSessionChanged -=
            _wifiProvisioningViewModel.SetSession;

        // Release the provisioning session.
        _wifiProvisioningViewModel.Deactivate();

        // Release the workspace session subscriptions.
        _viewModel.Deactivate();

        base.OnDisappearing();
    }
}