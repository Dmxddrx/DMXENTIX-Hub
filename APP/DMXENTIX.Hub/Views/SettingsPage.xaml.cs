using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(WifiProvisioningViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = viewModel;
    }
}