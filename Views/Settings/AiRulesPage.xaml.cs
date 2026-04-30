using VirtmaAi.Models.Entities;
using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class AiRulesPage : ContentPage
{
    private readonly AiRulesViewModel _vm;

    public AiRulesPage(AiRulesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }

    private void OnRuleToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is Switch sw && sw.BindingContext is AiRule rule)
        {
            if (_vm.ToggleEnabledCommand.CanExecute(rule))
                _vm.ToggleEnabledCommand.Execute(rule);
        }
    }
}
