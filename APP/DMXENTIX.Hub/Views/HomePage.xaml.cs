using Microsoft.Maui.Controls;
using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class HomePage : ContentPage
{
    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = viewModel;
    }
}