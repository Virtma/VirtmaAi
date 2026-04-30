using System.Globalization;

namespace VirtmaAi.ViewModels.Database;

public sealed class ResultRowJoinConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DbManagerViewModel.ResultRow row) return string.Empty;
        return string.Join("  |  ", row.Cells.Select(c => c is null ? "NULL" : (c.Length > 80 ? c[..80] + "…" : c)));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringJoinConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable<string> strs) return string.Join("  |  ", strs);
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
