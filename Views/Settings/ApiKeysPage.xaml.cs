using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class ApiKeysPage : ContentPage
{
    private readonly ApiKeysViewModel _vm;

    public ApiKeysPage(ApiKeysViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }
}
