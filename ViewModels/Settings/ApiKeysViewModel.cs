using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class ApiKeysViewModel : ViewModelBase
{
    private readonly IDatabaseService _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<ApiKeysViewModel> _logger;

    public ApiKeysViewModel(IDatabaseService db, ISettingsService settings, ILogger<ApiKeysViewModel> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public ObservableCollection<ApiKey> Keys { get; } = new();

    [ObservableProperty] private string _newServiceName = string.Empty;
    [ObservableProperty] private string _newKeyName = string.Empty;
    [ObservableProperty] private string _newValue = string.Empty;
    [ObservableProperty] private ApiKeyCategory _newCategory = ApiKeyCategory.Dev;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_db.Current is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var keys = await ctx.ApiKeys.OrderBy(k => k.ServiceName).ToListAsync();
            Keys.Clear();
            foreach (var k in keys) Keys.Add(k);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load API keys failed");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewServiceName) || string.IsNullOrWhiteSpace(NewKeyName) ||
            string.IsNullOrWhiteSpace(NewValue)) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var secureKey = $"virtmaai.apikey.{NewServiceName}.{NewKeyName}.{Guid.NewGuid():N}";
            var entity = new ApiKey
            {
                ServiceName = NewServiceName,
                KeyName = NewKeyName,
                SecureStorageKey = secureKey,
                Category = NewCategory
            };
            await _settings.SetSecretAsync(secureKey, NewValue);
            ctx.ApiKeys.Add(entity);
            await ctx.SaveChangesAsync();
            Keys.Add(entity);
            NewValue = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add API key failed");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task RemoveAsync(ApiKey? key)
    {
        if (key is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var entity = await ctx.ApiKeys.FindAsync(key.Id);
            if (entity is null) return;
            await _settings.RemoveSecretAsync(entity.SecureStorageKey);
            ctx.ApiKeys.Remove(entity);
            await ctx.SaveChangesAsync();
            Keys.Remove(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remove API key failed");
            ErrorMessage = ex.Message;
        }
    }
}
