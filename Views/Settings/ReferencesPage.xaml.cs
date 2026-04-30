using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class ReferencesPage : ContentPage
{
    public ReferencesPage(ReferencesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
