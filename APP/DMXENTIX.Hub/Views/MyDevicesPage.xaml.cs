using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class MyDevicesPage : ContentPage
{
    private readonly MyDevicesViewModel _viewModel;

    public MyDevicesPage(MyDevicesViewModel viewModel)
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