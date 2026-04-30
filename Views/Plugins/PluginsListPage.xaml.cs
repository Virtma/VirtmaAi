using VirtmaAi.ViewModels.Plugins;

namespace VirtmaAi.Views.Plugins;

public partial class PluginsListPage : ContentPage
{
    public PluginsListPage(PluginsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
