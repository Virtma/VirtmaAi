using VirtmaAi.ViewModels.Training;

namespace VirtmaAi.Views.Training;

public partial class ModelCreatorPage : ContentPage
{
    public ModelCreatorPage(ModelCreatorViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
