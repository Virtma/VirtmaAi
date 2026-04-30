using System.Globalization;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Notifications;

namespace VirtmaAi.Converters;

public sealed class StepEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class IsNotNullOrEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IsNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepShowNextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        return s is "Welcome" or "DataDirectory";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IsGreaterThanZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int i => i > 0,
            long l => l > 0,
            double d => d > 0,
            _ => false
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogKindToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            LogEntryKind.Error => Color.FromArgb("#E10600"),
            LogEntryKind.Warning => Color.FromArgb("#FFB020"),
            LogEntryKind.Info => Color.FromArgb("#3B82F6"),
            _ => Color.FromArgb("#888888")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ConversationModeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            ConversationMode.Chat => "Chat",
            ConversationMode.Code => "Code",
            ConversationMode.CoWork => "Co-Work",
            _ => string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "Chat" => ConversationMode.Chat,
            "Code" => ConversationMode.Code,
            "Co-Work" or "CoWork" => ConversationMode.CoWork,
            _ => ConversationMode.Chat
        };
    }
}

public sealed class ConversationModeNeedsDirConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConversationMode m && (m == ConversationMode.Code || m == ConversationMode.CoWork);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EqualsStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringEqualsInvertedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return true;
        return !string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MultiStringEqualsConverter : IMultiValueConverter
{
    public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var a = values[0]?.ToString();
        var b = values[1]?.ToString();
        if (a is null || b is null) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToSelectionModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SelectionMode.Multiple : SelectionMode.Single;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SelectionMode.Multiple;
}

public sealed class PercentToProgressConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            double d => Math.Clamp(d / 100.0, 0, 1),
            float f => Math.Clamp(f / 100f, 0, 1),
            int i => Math.Clamp(i / 100.0, 0, 1),
            _ => 0.0
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns just the file name (no directory path) from a full path string.
/// Used to display attached-file chips in the chat input area.
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? System.IO.Path.GetFileName(s) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
