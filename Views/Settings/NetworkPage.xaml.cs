using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class NetworkPage : ContentPage
{
    private readonly NetworkViewModel _vm;

    public NetworkPage(NetworkViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.RefreshAsync();
    }
}
