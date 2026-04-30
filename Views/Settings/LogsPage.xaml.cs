using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class LogsPage : ContentPage
{
    public LogsPage(LogsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
