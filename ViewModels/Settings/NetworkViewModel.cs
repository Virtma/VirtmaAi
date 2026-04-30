using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.System;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class NetworkViewModel : ViewModelBase
{
    private readonly IDatabaseService _db;
    private readonly INetworkInterfaceService _network;
    private readonly IToastService _toast;
    private readonly ILogger<NetworkViewModel> _logger;

    public NetworkViewModel(
        IDatabaseService db,
        INetworkInterfaceService network,
        IToastService toast,
        ILogger<NetworkViewModel> logger)
    {
        _db = db;
        _network = network;
        _toast = toast;
        _logger = logger;
    }

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = new();

    [ObservableProperty] private NetworkInterfaceInfo? _selected;
    [ObservableProperty] private string? _publicIp;
    [ObservableProperty] private string? _activeInterfaceName;
    [ObservableProperty] private DateTime? _activeSavedAt;

    public bool HasActiveInterface => !string.IsNullOrWhiteSpace(ActiveInterfaceName);

    public string ActiveBannerText => HasActiveInterface
        ? $"Active: {ActiveInterfaceName}"
        : "No active interface — pick one and click Save";

    partial void OnActiveInterfaceNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasActiveInterface));
        OnPropertyChanged(nameof(ActiveBannerText));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            Interfaces.Clear();
            foreach (var nic in _network.Enumerate().Where(n => n.IsUp))
                Interfaces.Add(nic);

            if (_db.Current is not null)
            {
                await using var ctx = _db.CreateContext();
                var pref = await ctx.NetworkPreferences.OrderByDescending(p => p.UpdatedAt).FirstOrDefaultAsync();
                if (pref is not null)
                {
                    ActiveInterfaceName = pref.PreferredInterface;
                    ActiveSavedAt = pref.UpdatedAt;
                    Selected = Interfaces.FirstOrDefault(i => i.Name == pref.PreferredInterface);
                }
                else
                {
                    ActiveInterfaceName = null;
                    ActiveSavedAt = null;
                }
            }

            PublicIp = await _network.GetPublicIpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Network refresh failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Refresh failed: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Selected is null)
        {
            await _toast.WarningAsync("Pick an interface first.");
            return;
        }
        try
        {
            await using var ctx = _db.CreateContext();
            var pref = await ctx.NetworkPreferences.OrderByDescending(p => p.UpdatedAt).FirstOrDefaultAsync();
            if (pref is null)
            {
                pref = new NetworkPreference();
                ctx.NetworkPreferences.Add(pref);
            }
            pref.PreferredInterface = Selected.Name;
            pref.CurrentPublicIp = PublicIp;
            pref.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();

            ActiveInterfaceName = Selected.Name;
            ActiveSavedAt = pref.UpdatedAt;
            await _toast.SuccessAsync($"Active interface set: {Selected.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Network save failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Save failed: " + ex.Message);
        }
    }
}
