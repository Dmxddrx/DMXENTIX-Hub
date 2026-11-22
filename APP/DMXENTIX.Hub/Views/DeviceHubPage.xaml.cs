using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class DeviceHubPage : ContentPage
{
    private readonly DeviceHubViewModel _viewModel;

    public DeviceHubPage(DeviceHubViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _viewModel.SetDiscoveryPageVisible(true);

        await _viewModel.InitializeAsync();
    }
    protected override void OnDisappearing()
    {
        _viewModel.SetDiscoveryPageVisible(false);

        base.OnDisappearing();
    }
}