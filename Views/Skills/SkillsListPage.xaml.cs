using VirtmaAi.Models.Entities;
using VirtmaAi.ViewModels.Skills;

namespace VirtmaAi.Views.Skills;

public partial class SkillsListPage : ContentPage
{
    private readonly SkillsViewModel _vm;
    // Suppresses toggle events fired by data-binding initialization.
    // Without this flag, loading N skills fires N "toggle" events → N DB writes + N toasts.
    private bool _suppressToggle;

    public SkillsListPage(SkillsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _suppressToggle = true;
        try   { await _vm.LoadAsync(); }
        finally { _suppressToggle = false; }
    }

    private void OnSkillEnabledToggled(object? sender, ToggledEventArgs e)
    {
        // Ignore binding-init events.
        if (_suppressToggle) return;

        if (sender is Switch sw && sw.BindingContext is Skill skill)
        {
            if (_vm.ToggleEnabledCommand.CanExecute(skill))
                _vm.ToggleEnabledCommand.Execute(skill);
        }
    }
}
