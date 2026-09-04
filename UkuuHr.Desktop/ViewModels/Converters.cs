using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UkuuHr.Sync.Services;

namespace UkuuHr.Sync.ViewModels;

/// <summary>
/// Converts a LogLevel to a foreground color for the log text.
/// </summary>
public class LogLevelConverter : IValueConverter
{
    public static readonly LogLevelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            LogLevel.Success => new SolidColorBrush(Color.Parse("#10B981")),
            LogLevel.Warning => new SolidColorBrush(Color.Parse("#F59E0B")),
            LogLevel.Error   => new SolidColorBrush(Color.Parse("#EF4444")),
            _                => new SolidColorBrush(Color.Parse("#3B82F6"))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}

/// <summary>
/// Maps a connection boolean to a status color — green when connected,
/// amber when offline/failed.
/// </summary>
public class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ok = value is true;
        return new SolidColorBrush(Color.Parse(ok ? "#34D399" : "#FBBF24"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}
