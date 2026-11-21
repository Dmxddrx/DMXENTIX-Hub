using EMOTIX360.App.ViewModels;

namespace EMOTIX360.App.Views;

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

        await _viewModel.InitializeAsync();
    }
}