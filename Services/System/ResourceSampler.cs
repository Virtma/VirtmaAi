using System.Diagnostics;
using System.Runtime.Versioning;

namespace VirtmaAi.Services.System;

public sealed class ResourceSampler : IResourceSampler
{
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleTime;
    private readonly object _gate = new();

#if WINDOWS
    // GPU performance counters: lazily created. Windows exposes per-engine "GPU Engine" counters
    // (3D, Compute, Copy, …) and per-adapter "GPU Adapter Memory" counters. We aggregate by adapter
    // name (LUID-derived) so multiple GPUs surface as separate rows.
    private List<PerformanceCounter>? _engineCounters;
    private List<PerformanceCounter>? _memoryCounters;
    private DateTime _gpuCountersInitializedAt = DateTime.MinValue;
#endif

    public ResourceSample Sample()
    {
        lock (_gate)
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();

            var now = DateTime.UtcNow;
            var cpuNow = proc.TotalProcessorTime;
            double cpuPercent = 0;
            if (_lastSampleTime != default)
            {
                var elapsed = now - _lastSampleTime;
                var cpuDelta = cpuNow - _lastCpuTime;
                if (elapsed.TotalMilliseconds > 0)
                {
                    cpuPercent = cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
                    if (cpuPercent < 0) cpuPercent = 0;
                    if (cpuPercent > 100) cpuPercent = 100;
                }
            }
            _lastCpuTime = cpuNow;
            _lastSampleTime = now;

            var working = proc.WorkingSet64;
            var managed = GC.GetTotalMemory(forceFullCollection: false);
            var gcInfo = GC.GetGCMemoryInfo();
            var totalSystem = gcInfo.TotalAvailableMemoryBytes;
            var available = totalSystem - gcInfo.MemoryLoadBytes;
            if (available < 0) available = 0;

            var gpus = SampleGpus();
            return new ResourceSample(now, cpuPercent, working, managed, totalSystem, available, gpus);
        }
    }

    private GpuSample[] SampleGpus()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
            return SampleGpusWindows();
#endif
        return Array.Empty<GpuSample>();
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private GpuSample[] SampleGpusWindows()
    {
        try
        {
            EnsureGpuCounters();
            if (_engineCounters is null) return Array.Empty<GpuSample>();

            // Aggregate engine utilization by adapter id (the "luid_..._phys_X" prefix in the instance name).
            // Each engine counter is something like: pid_1234_luid_0x00000000_0x000164DA_phys_0_eng_0_engtype_3D.
            var perAdapterEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _engineCounters)
            {
                try
                {
                    double v = c.NextValue();
                    var key = AdapterKey(c.InstanceName);
                    if (perAdapterEngine.TryGetValue(key, out var existing))
                        perAdapterEngine[key] = Math.Max(existing, v);
                    else
                        perAdapterEngine[key] = v;
                }
                catch { /* counter may go away if device hot-plugs */ }
            }

            var perAdapterMem = new Dictionary<string, (long used, long total)>(StringComparer.OrdinalIgnoreCase);
            if (_memoryCounters is not null)
            {
                foreach (var c in _memoryCounters)
                {
                    try
                    {
                        var key = AdapterKey(c.InstanceName);
                        long bytes = (long)c.NextValue();
                        if (!perAdapterMem.TryGetValue(key, out var tup)) tup = (0, 0);
                        // "Dedicated Usage" → used; "Dedicated" or similar without "Usage" → total budget.
                        if (c.CounterName.Contains("Usage", StringComparison.OrdinalIgnoreCase))
                            tup.used = bytes;
                        else
                            tup.total = Math.Max(tup.total, bytes);
                        perAdapterMem[key] = tup;
                    }
                    catch { }
                }
            }

            var samples = new List<GpuSample>(perAdapterEngine.Count);
            foreach (var (key, util) in perAdapterEngine)
            {
                long used = 0, total = 0;
                if (perAdapterMem.TryGetValue(key, out var mem)) { used = mem.used; total = mem.total; }
                samples.Add(new GpuSample(key, Math.Clamp(util, 0, 100), used, total));
            }
            return samples.ToArray();
        }
        catch
        {
            return Array.Empty<GpuSample>();
        }
    }

    [SupportedOSPlatform("windows")]
    private void EnsureGpuCounters()
    {
        // Refresh every 30s in case the user enabled/disabled a GPU.
        if (_engineCounters is not null && (DateTime.UtcNow - _gpuCountersInitializedAt) < TimeSpan.FromSeconds(30))
            return;

        try
        {
            DisposeCounters();
            var engine = new List<PerformanceCounter>();
            var memory = new List<PerformanceCounter>();

            var engineCat = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in engineCat.GetInstanceNames())
            {
                // "Utilization Percentage" is the only one we need; per-engine, summed by adapter.
                if (!instance.Contains("engtype", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    engine.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true));
                }
                catch { }
            }

            try
            {
                var memCat = new PerformanceCounterCategory("GPU Adapter Memory");
                foreach (var instance in memCat.GetInstanceNames())
                {
                    foreach (var counterName in new[] { "Dedicated Usage", "Total Committed" })
                    {
                        try { memory.Add(new PerformanceCounter("GPU Adapter Memory", counterName, instance, readOnly: true)); }
                        catch { }
                    }
                }
            }
            catch { }

            // First call to NextValue() returns 0 for many counters; prime them so the next sample
            // returns real data.
            foreach (var c in engine) try { c.NextValue(); } catch { }
            foreach (var c in memory) try { c.NextValue(); } catch { }

            _engineCounters = engine;
            _memoryCounters = memory;
            _gpuCountersInitializedAt = DateTime.UtcNow;
        }
        catch { /* perf counters not available — leave _engineCounters null */ }
    }

    [SupportedOSPlatform("windows")]
    private void DisposeCounters()
    {
        if (_engineCounters is not null)
            foreach (var c in _engineCounters) try { c.Dispose(); } catch { }
        if (_memoryCounters is not null)
            foreach (var c in _memoryCounters) try { c.Dispose(); } catch { }
        _engineCounters = null;
        _memoryCounters = null;
    }

    private static string AdapterKey(string instanceName)
    {
        // Pull out the LUID + phys_X portion which uniquely identifies an adapter.
        // Example: pid_1234_luid_0x00000000_0x000164DA_phys_0_eng_0_engtype_3D
        var luidIdx = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (luidIdx < 0) return instanceName;
        var rest = instanceName[luidIdx..];
        var engIdx = rest.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
        return engIdx < 0 ? rest : rest[..engIdx];
    }
#endif
}
