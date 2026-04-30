namespace VirtmaAi.Services.System;

public interface IResourceSampler
{
    ResourceSample Sample();
}

public sealed record ResourceSample(
    DateTime TimestampUtc,
    double AppCpuPercent,
    long AppWorkingSetBytes,
    long AppManagedHeapBytes,
    long SystemTotalMemoryBytes,
    long SystemAvailableMemoryBytes,
    GpuSample[] Gpus);

/// <summary>
/// Snapshot of a single GPU's utilization. <see cref="UsagePercent"/> is the rolled-up engine
/// utilization (max across 3D/Compute/Copy on Windows) and <see cref="DedicatedMemoryUsedBytes"/>
/// is the current dedicated VRAM in use.
/// </summary>
public sealed record GpuSample(
    string Name,
    double UsagePercent,
    long DedicatedMemoryUsedBytes,
    long DedicatedMemoryTotalBytes);
