using System.Collections.Generic;
using DMXENTIX.Hub.ViewModels;


namespace DMXENTIX.Hub.Views;

public partial class DeviceWorkspacePage : ContentPage, IQueryAttributable
{
    private readonly DeviceWorkspaceViewModel _viewModel;
    private readonly WifiProvisioningViewModel _wifiProvisioningViewModel;

    private string? _deviceId;

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