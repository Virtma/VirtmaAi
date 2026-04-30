namespace VirtmaAi.Services.Themes;

public interface IThemeService
{
    ThemeDefinition Active { get; }
    event EventHandler<ThemeDefinition>? ThemeChanged;

    IReadOnlyList<ThemeDefinition> GetBuiltIn();
    Task<IReadOnlyList<ThemeDefinition>> GetUserAsync();

    Task ApplyAsync(ThemeDefinition theme);
    Task RestoreActiveAsync();
    Task SaveAsync(ThemeDefinition theme);
    Task DeleteAsync(Guid id);

    Task<ThemeDefinition> ImportFromJsonAsync(string json);
    string ExportToJson(ThemeDefinition theme);

    bool TryDetectThemeBlock(string message, out string? json);
}
