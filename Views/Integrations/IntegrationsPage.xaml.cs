using VirtmaAi.ViewModels.Integrations;

namespace VirtmaAi.Views.Integrations;

public partial class IntegrationsPage : ContentPage
{
    public IntegrationsPage(IntegrationsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
