using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Themes;

public sealed class ThemeService : IThemeService
{
    private const string ActiveThemeKey = "theme.active.name";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex ThemeBlockRegex = new(
        @"```(?:json|vtheme)?\s*(\{[\s\S]*?""schema""\s*:\s*""vtheme/v1""[\s\S]*?\})\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IDatabaseService _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<ThemeService> _logger;
    private ThemeDefinition _active;

    public ThemeService(IDatabaseService db, ISettingsService settings, ILogger<ThemeService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
        _active = BuiltInDark();
    }

    public ThemeDefinition Active => _active;
    public event EventHandler<ThemeDefinition>? ThemeChanged;

    public IReadOnlyList<ThemeDefinition> GetBuiltIn()
        => new[] { BuiltInDark(), BuiltInLight() };

    public async Task<IReadOnlyList<ThemeDefinition>> GetUserAsync()
    {
        if (_db.Current is null) return Array.Empty<ThemeDefinition>();
        try
        {
            await using var ctx = _db.CreateContext();
            var rows = ctx.Themes.AsQueryable().ToList();
            var list = new List<ThemeDefinition>(rows.Count);
            foreach (var row in rows)
            {
                var def = JsonSerializer.Deserialize<ThemeDefinition>(row.JsonDefinition, JsonOpts);
                if (def is not null)
                {
                    def.Name = row.Name;
                    list.Add(def);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load user themes failed");
            return Array.Empty<ThemeDefinition>();
        }
    }

    public Task ApplyAsync(ThemeDefinition theme)
    {
        _active = theme;
        ApplyToResources(theme);
        try { _settings.Set(ActiveThemeKey, theme.Name); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persist active theme name failed"); }
        ThemeChanged?.Invoke(this, theme);

        // StaticResource bindings don't refresh when their underlying resource value changes.
        // Force a live rebuild of the visual tree so every page picks up the new colors. We do
        // this on the dispatcher so the resource update lands first.
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (app is not null && dispatcher is not null)
        {
            dispatcher.Dispatch(() =>
            {
                try
                {
                    var window = app.Windows.FirstOrDefault();
                    if (window is null) return;
                    // Only swap if we're already inside the main shell — don't disrupt FirstRun.
                    if (window.Page is Shell)
                        window.Page = new AppShell();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Theme refresh root page failed"); }
            });
        }
        return Task.CompletedTask;
    }

    public async Task RestoreActiveAsync()
    {
        try
        {
            var name = _settings.Get<string>(ActiveThemeKey);
            if (string.IsNullOrWhiteSpace(name)) { ApplyToResources(_active); return; }

            var builtIn = GetBuiltIn().FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (builtIn is not null) { await ApplyAsync(builtIn); return; }

            var user = await GetUserAsync();
            var match = user.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) { await ApplyAsync(match); return; }

            ApplyToResources(_active);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restore active theme failed");
            ApplyToResources(_active);
        }
    }

    public async Task SaveAsync(ThemeDefinition theme)
    {
        if (_db.Current is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var existing = ctx.Themes.FirstOrDefault(t => t.Name == theme.Name);
            var json = JsonSerializer.Serialize(theme, JsonOpts);
            if (existing is null)
            {
                ctx.Themes.Add(new Theme { Name = theme.Name, JsonDefinition = json });
            }
            else
            {
                existing.JsonDefinition = json;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save theme failed");
            throw;
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (_db.Current is null) return;
        await using var ctx = _db.CreateContext();
        var row = await ctx.Themes.FindAsync(id);
        if (row is null) return;
        ctx.Themes.Remove(row);
        await ctx.SaveChangesAsync();
    }

    public Task<ThemeDefinition> ImportFromJsonAsync(string json)
    {
        var def = JsonSerializer.Deserialize<ThemeDefinition>(json, JsonOpts)
            ?? throw new InvalidDataException("theme json did not parse");
        if (!string.Equals(def.Schema, "vtheme/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("unsupported theme schema: " + def.Schema);
        return Task.FromResult(def);
    }

    public string ExportToJson(ThemeDefinition theme)
        => JsonSerializer.Serialize(theme, JsonOpts);

    public bool TryDetectThemeBlock(string message, out string? json)
    {
        json = null;
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = ThemeBlockRegex.Match(message);
        if (m.Success) { json = m.Groups[1].Value; return true; }

        var trimmed = message.TrimStart();
        if (trimmed.StartsWith("{") && trimmed.Contains("\"schema\"") && trimmed.Contains("vtheme/v1"))
        {
            json = message;
            return true;
        }
        return false;
    }

    private static void ApplyToResources(ThemeDefinition theme)
    {
        var app = Application.Current;
        if (app?.Resources is null) return;
        var r = app.Resources;

        r["Primary"] = Color.FromArgb(theme.Palette.Primary);
        r["PrimaryDark"] = Color.FromArgb(theme.Palette.PrimaryPressed);
        r["Secondary"] = Color.FromArgb(theme.Palette.Secondary);
        r["SurfaceDark"] = Color.FromArgb(theme.Palette.SurfaceBase);
        r["SurfaceDarkAlt"] = Color.FromArgb(theme.Palette.SurfaceAlt);
        r["SurfaceLight"] = Color.FromArgb(theme.BaseMode == ThemeBaseMode.Light
            ? theme.Palette.SurfaceBase
            : "#FFFFFF");
        r["SurfaceLightAlt"] = Color.FromArgb(theme.BaseMode == ThemeBaseMode.Light
            ? theme.Palette.SurfaceAlt
            : "#F5F5F5");
        r["Accent"] = Color.FromArgb(theme.Palette.Primary);
        r["AccentPressed"] = Color.FromArgb(theme.Palette.PrimaryPressed);
        r["AccentInteractive"] = Color.FromArgb(theme.Palette.Secondary);

        r["TextPrimary"] = Color.FromArgb(theme.Palette.OnSurface);
        r["TextMuted"] = Color.FromArgb(theme.Palette.OnSurfaceMuted);
        r["TextFaint"] = Color.FromArgb(theme.Palette.OnSurfaceFaint);

        r["Gray300"] = Color.FromArgb(theme.Palette.OnSurfaceMuted);
        r["Gray500"] = Color.FromArgb(theme.Palette.OnSurfaceFaint);
        r["Gray600"] = Color.FromArgb(theme.Palette.Border);

        app.UserAppTheme = theme.BaseMode == ThemeBaseMode.Light ? AppTheme.Light : AppTheme.Dark;
    }

    public static ThemeDefinition BuiltInDark() => new()
    {
        Name = "VirtmaAi Dark",
        Description = "Default cinematic black + red",
        BaseMode = ThemeBaseMode.Dark,
        Palette = new ThemePalette()
    };

    public static ThemeDefinition BuiltInLight() => new()
    {
        Name = "VirtmaAi Light",
        Description = "Light surfaces with signature red accent",
        BaseMode = ThemeBaseMode.Light,
        Palette = new ThemePalette
        {
            Primary = "#E10600",
            PrimaryPressed = "#B00000",
            Secondary = "#FF3B30",
            SurfaceBase = "#FFFFFF",
            SurfaceAlt = "#F5F5F5",
            SurfaceElevated = "#EAEAEA",
            OnSurface = "#0A0A0A",
            OnSurfaceMuted = "#555555",
            OnSurfaceFaint = "#888888",
            Border = "#D0D0D0"
        }
    };
}
