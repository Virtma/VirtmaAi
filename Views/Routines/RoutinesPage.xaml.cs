using VirtmaAi.ViewModels.Routines;

namespace VirtmaAi.Views.Routines;

public partial class RoutinesPage : ContentPage
{
    public RoutinesPage(RoutinesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
