using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class ExternalApiKeysPage : ContentPage
{
    public ExternalApiKeysPage(ExternalApiKeysViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
