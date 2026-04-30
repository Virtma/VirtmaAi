using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class ThemesPage : ContentPage
{
    public ThemesPage(ThemesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
