namespace VirtmaAi.Services.System;

public interface IHardwareProbe
{
    HardwareInfo Probe();
}

public sealed record HardwareInfo(
    string OsDescription,
    string Architecture,
    int LogicalProcessors,
    long TotalMemoryBytes,
    long AvailableDiskBytes);
