using System.Runtime.InteropServices;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.System;

public sealed class HardwareProbe : IHardwareProbe
{
    private readonly ISettingsService _settings;

    public HardwareProbe(ISettingsService settings)
    {
        _settings = settings;
    }

    public HardwareInfo Probe()
    {
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture.ToString();
        var cpus = Environment.ProcessorCount;
        var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var disk = ProbeDisk();
        return new HardwareInfo(os, arch, cpus, totalMem, disk);
    }

    private long ProbeDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_settings.DataDirectory) ?? Directory.GetCurrentDirectory());
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
