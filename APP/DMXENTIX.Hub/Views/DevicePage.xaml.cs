using Microsoft.Maui.Controls;
using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class DevicePage : ContentPage
{
    public DevicePage(DeviceViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
