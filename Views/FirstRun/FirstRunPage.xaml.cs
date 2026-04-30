using VirtmaAi.ViewModels.FirstRun;

namespace VirtmaAi.Views.FirstRun;

public partial class FirstRunPage : ContentPage
{
    public FirstRunPage(FirstRunViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
