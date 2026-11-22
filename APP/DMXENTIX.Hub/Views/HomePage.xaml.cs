using DMXENTIX.Hub.ViewModels;

namespace DMXENTIX.Hub.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;

        BindingContext = _viewModel;
    }

    // ========================================================
    // REFRESH HOME WHEN IT BECOMES VISIBLE
    // ========================================================

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await _viewModel.InitializeAsync();
    }

    // ========================================================
    // FIND DEVICE
    // ========================================================

    private async void OnFindDeviceClicked(
        object sender,
        EventArgs e)
    {
        Shell? shell = Shell.Current;

        if (shell is null)
            return;

        await shell.GoToAsync("devices");
    }

    // ========================================================
    // CLOSE INFORMATION POPUP FROM ANDROID BACK
    // ========================================================

    public bool TryCloseInfoPopup()
    {
        if (!_viewModel.IsInfoPopupVisible)
            return false;

        _viewModel.CloseInformationPopup();

        return true;
    }
}