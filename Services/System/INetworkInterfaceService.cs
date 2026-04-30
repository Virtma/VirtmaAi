namespace VirtmaAi.Services.System;

public interface INetworkInterfaceService
{
    IReadOnlyList<NetworkInterfaceInfo> Enumerate();
    Task<string?> GetPublicIpAsync(CancellationToken cancellationToken = default);
}

public sealed record NetworkInterfaceInfo(
    string Name,
    string Description,
    string InterfaceType,
    bool IsUp,
    IReadOnlyList<string> Addresses);
