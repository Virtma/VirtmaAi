using VirtmaAi.ViewModels.Models;

namespace VirtmaAi.Views.Models;

public partial class ModelLibraryPage : ContentPage
{
    private readonly ModelLibraryViewModel _vm;

    public ModelLibraryPage(ModelLibraryViewModel vm)
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
