using System.Collections.Generic;
using EMOTIX360.App.ViewModels;

namespace EMOTIX360.App.Views;

public partial class DeviceWorkspacePage : ContentPage, IQueryAttributable
{
    private readonly DeviceWorkspaceViewModel _viewModel;

    private string? _deviceId;

    public DeviceWorkspacePage(
        DeviceWorkspaceViewModel viewModel,
        HomeViewModel homeViewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = _viewModel;

        // The workspace dashboard uses the existing
        // session-aware EMOTIX360 dashboard ViewModel.
        Emotix360Dashboard.BindingContext = homeViewModel;

        // The workspace already displays its own connection information.
        Emotix360Dashboard.ShowDeviceConnectionCard = false;
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
        _viewModel.Deactivate();

        base.OnDisappearing();
    }
}