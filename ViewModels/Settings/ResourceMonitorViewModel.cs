using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.System;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class ResourceMonitorViewModel : ViewModelBase
{
    private const int MaxSamples = 120;

    private readonly IResourceSampler _sampler;
    private readonly IHardwareProbe _hardware;
    private readonly ILogger<ResourceMonitorViewModel> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public ResourceMonitorViewModel(
        IResourceSampler sampler,
        IHardwareProbe hardware,
        ILogger<ResourceMonitorViewModel> logger)
    {
        _sampler = sampler;
        _hardware = hardware;
        _logger = logger;
    }

    public ObservableCollection<ResourceSample> History { get; } = new();
    public ObservableCollection<GpuRow> Gpus { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _intervalSeconds = 2;
    [ObservableProperty] private string _hardwareSummary = string.Empty;
    [ObservableProperty] private double _currentCpuPercent;
    [ObservableProperty] private long _currentWorkingSet;
    [ObservableProperty] private long _currentManagedHeap;
    [ObservableProperty] private long _systemTotal;
    [ObservableProperty] private long _systemAvailable;

    public string CpuLabel => $"{CurrentCpuPercent:0.0}%";
    public string WorkingSetLabel => Bytes(CurrentWorkingSet);
    public string ManagedHeapLabel => Bytes(CurrentManagedHeap);
    public string SystemTotalLabel => Bytes(SystemTotal);
    public string SystemAvailableLabel => Bytes(SystemAvailable);
    public string SystemUsedLabel => Bytes(SystemTotal - SystemAvailable);

    public double SystemMemoryUsedPercent
        => SystemTotal > 0 ? (double)(SystemTotal - SystemAvailable) / SystemTotal * 100.0 : 0;

    partial void OnCurrentCpuPercentChanged(double value) => OnPropertyChanged(nameof(CpuLabel));
    partial void OnCurrentWorkingSetChanged(long value) => OnPropertyChanged(nameof(WorkingSetLabel));
    partial void OnCurrentManagedHeapChanged(long value) => OnPropertyChanged(nameof(ManagedHeapLabel));
    partial void OnSystemTotalChanged(long value)
    {
        OnPropertyChanged(nameof(SystemTotalLabel));
        OnPropertyChanged(nameof(SystemUsedLabel));
        OnPropertyChanged(nameof(SystemMemoryUsedPercent));
    }
    partial void OnSystemAvailableChanged(long value)
    {
        OnPropertyChanged(nameof(SystemAvailableLabel));
        OnPropertyChanged(nameof(SystemUsedLabel));
        OnPropertyChanged(nameof(SystemMemoryUsedPercent));
    }

    public void StartMonitoring()
    {
        if (IsRunning) return;
        var hw = _hardware.Probe();
        HardwareSummary = $"{hw.OsDescription} \u00B7 {hw.LogicalProcessors} threads \u00B7 {Bytes(hw.TotalMemoryBytes)} RAM";
        _loopCts = new CancellationTokenSource();
        IsRunning = true;
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public void StopMonitoring()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _loopCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    public void Clear()
    {
        History.Clear();
        CurrentCpuPercent = 0;
        CurrentWorkingSet = 0;
        CurrentManagedHeap = 0;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // discard the first sample (no baseline yet)
            _sampler.Sample();
            while (!ct.IsCancellationRequested)
            {
                var delayMs = (int)Math.Clamp(IntervalSeconds * 1000, 250, 60_000);
                try { await Task.Delay(delayMs, ct); }
                catch (TaskCanceledException) { break; }

                if (ct.IsCancellationRequested) break;

                var sample = _sampler.Sample();
                await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(() =>
                {
                    History.Add(sample);
                    while (History.Count > MaxSamples) History.RemoveAt(0);
                    CurrentCpuPercent = sample.AppCpuPercent;
                    CurrentWorkingSet = sample.AppWorkingSetBytes;
                    CurrentManagedHeap = sample.AppManagedHeapBytes;
                    SystemTotal = sample.SystemTotalMemoryBytes;
                    SystemAvailable = sample.SystemAvailableMemoryBytes;
                    UpdateGpuRows(sample.Gpus);
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resource monitor loop");
        }
    }

    private void UpdateGpuRows(GpuSample[] samples)
    {
        // Reconcile the bound list against the latest sample. We update existing rows in-place
        // (so the UI doesn't flicker) and add/remove only when the GPU set actually changes.
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            GpuRow? row = i < Gpus.Count ? Gpus[i] : null;
            if (row is null)
            {
                Gpus.Add(new GpuRow { Name = s.Name, UsagePercent = s.UsagePercent, MemoryUsedBytes = s.DedicatedMemoryUsedBytes, MemoryTotalBytes = s.DedicatedMemoryTotalBytes });
            }
            else
            {
                row.Name = s.Name;
                row.UsagePercent = s.UsagePercent;
                row.MemoryUsedBytes = s.DedicatedMemoryUsedBytes;
                row.MemoryTotalBytes = s.DedicatedMemoryTotalBytes;
            }
        }
        while (Gpus.Count > samples.Length) Gpus.RemoveAt(Gpus.Count - 1);
    }

    private static string Bytes(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes;
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }
}

public sealed partial class GpuRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private double _usagePercent;
    [ObservableProperty] private long _memoryUsedBytes;
    [ObservableProperty] private long _memoryTotalBytes;

    public string UsageLabel => $"{UsagePercent:0.0}%";
    public string MemoryLabel => MemoryTotalBytes > 0
        ? $"{ResourceMonitorViewModel_Bytes(MemoryUsedBytes)} / {ResourceMonitorViewModel_Bytes(MemoryTotalBytes)}"
        : ResourceMonitorViewModel_Bytes(MemoryUsedBytes);
    public double MemoryFraction => MemoryTotalBytes > 0 ? (double)MemoryUsedBytes / MemoryTotalBytes : 0;

    partial void OnUsagePercentChanged(double value) => OnPropertyChanged(nameof(UsageLabel));
    partial void OnMemoryUsedBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MemoryLabel));
        OnPropertyChanged(nameof(MemoryFraction));
    }
    partial void OnMemoryTotalBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MemoryLabel));
        OnPropertyChanged(nameof(MemoryFraction));
    }

    private static string ResourceMonitorViewModel_Bytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes;
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }
}
