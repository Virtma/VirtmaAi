using VirtmaAi.ViewModels.Database;

namespace VirtmaAi.Views.Database;

public partial class DbManagerPage : ContentPage
{
    public DbManagerPage(DbManagerViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        _ = vm.LoadAsync();
    }
}
