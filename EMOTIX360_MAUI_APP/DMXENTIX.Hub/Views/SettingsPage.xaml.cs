using EMOTIX360.App.ViewModels;

namespace EMOTIX360.App.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(WifiProvisioningViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = viewModel;
    }
}