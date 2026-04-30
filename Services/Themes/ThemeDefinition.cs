using System.Text.Json.Serialization;

namespace VirtmaAi.Services.Themes;

public sealed class ThemeDefinition
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "vtheme/v1";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("baseMode")]
    public ThemeBaseMode BaseMode { get; set; } = ThemeBaseMode.Dark;

    [JsonPropertyName("palette")]
    public ThemePalette Palette { get; set; } = new();

    [JsonPropertyName("typography")]
    public ThemeTypography Typography { get; set; } = new();

    [JsonPropertyName("spacing")]
    public ThemeSpacing Spacing { get; set; } = new();

    [JsonPropertyName("radii")]
    public ThemeRadii Radii { get; set; } = new();
}

public enum ThemeBaseMode { Dark, Light }

public sealed class ThemePalette
{
    public string Primary { get; set; } = "#E10600";
    public string PrimaryPressed { get; set; } = "#B00000";
    public string Secondary { get; set; } = "#FF3B30";
    public string SurfaceBase { get; set; } = "#0A0A0A";
    public string SurfaceAlt { get; set; } = "#141414";
    public string SurfaceElevated { get; set; } = "#1C1C1C";
    public string OnSurface { get; set; } = "#FFFFFF";
    public string OnSurfaceMuted { get; set; } = "#ACACAC";
    public string OnSurfaceFaint { get; set; } = "#6E6E6E";
    public string Border { get; set; } = "#404040";
    public string Error { get; set; } = "#FF3B30";
    public string Warning { get; set; } = "#FFB020";
    public string Success { get; set; } = "#30D158";
    public string Info { get; set; } = "#3BA9FF";
}

public sealed class ThemeTypography
{
    public string FontFamily { get; set; } = "OpenSansRegular";
    public string FontFamilyBold { get; set; } = "OpenSansSemibold";
    public double BaseSize { get; set; } = 14;
    public double SmallSize { get; set; } = 12;
    public double LargeSize { get; set; } = 18;
    public double HeadingSize { get; set; } = 22;
}

public sealed class ThemeSpacing
{
    public double Xs { get; set; } = 4;
    public double Sm { get; set; } = 8;
    public double Md { get; set; } = 12;
    public double Lg { get; set; } = 16;
    public double Xl { get; set; } = 24;
}

public sealed class ThemeRadii
{
    public double Sm { get; set; } = 4;
    public double Md { get; set; } = 8;
    public double Lg { get; set; } = 12;
}
