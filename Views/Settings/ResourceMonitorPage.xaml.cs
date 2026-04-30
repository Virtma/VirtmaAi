using System.Collections.Specialized;
using VirtmaAi.Services.System;
using VirtmaAi.ViewModels.Settings;

namespace VirtmaAi.Views.Settings;

public partial class ResourceMonitorPage : ContentPage
{
    private readonly ResourceMonitorViewModel _vm;
    private ResourceSparklineDrawable? _cpuDrawable;
    private ResourceSparklineDrawable? _memDrawable;

    public ResourceMonitorPage(ResourceMonitorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        _cpuDrawable = (ResourceSparklineDrawable)Resources["CpuDrawable"];
        _memDrawable = (ResourceSparklineDrawable)Resources["MemDrawable"];
        _cpuDrawable.Maximum = 100;
        _cpuDrawable.LineColor = Color.FromArgb("#E10600");
        _cpuDrawable.FillColor = Color.FromArgb("#33E10600");

        _memDrawable.LineColor = Color.FromArgb("#3B82F6");
        _memDrawable.FillColor = Color.FromArgb("#333B82F6");

        _vm.History.CollectionChanged += OnHistoryChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.StartMonitoring();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.StopMonitoring();
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_cpuDrawable is null || _memDrawable is null) return;

        _cpuDrawable.Values = _vm.History.Select(s => s.AppCpuPercent).ToList();

        var memValues = _vm.History.Select(s => (double)s.AppWorkingSetBytes).ToList();
        _memDrawable.Values = memValues;
        _memDrawable.Maximum = memValues.Count > 0 ? Math.Max(memValues.Max() * 1.1, 1) : 1;

        Dispatcher.Dispatch(() =>
        {
            CpuChart.Invalidate();
            MemChart.Invalidate();
        });
    }
}
