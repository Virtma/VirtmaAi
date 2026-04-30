using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.System;

public sealed class NetworkInterfaceService : INetworkInterfaceService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NetworkInterfaceService> _logger;

    public NetworkInterfaceService(IHttpClientFactory httpFactory, ILogger<NetworkInterfaceService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public IReadOnlyList<NetworkInterfaceInfo> Enumerate()
    {
        var list = new List<NetworkInterfaceInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address.ToString())
                    .ToList();
                list.Add(new NetworkInterfaceInfo(
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType.ToString(),
                    nic.OperationalStatus == OperationalStatus.Up,
                    addresses));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerate network interfaces failed");
        }
        return list;
    }

    public async Task<string?> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var ip = await http.GetStringAsync("https://api.ipify.org", cancellationToken).ConfigureAwait(false);
            return ip.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Public IP lookup failed");
            return null;
        }
    }
}
