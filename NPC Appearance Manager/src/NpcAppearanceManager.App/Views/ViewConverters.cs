using System.Globalization;
using System.Windows.Data;

namespace NpcAppearanceManager.App.Views;

/// <summary>True when an enum value's name equals the converter parameter.</summary>
public sealed class EnumMatch : IValueConverter
{
    public static readonly EnumMatch Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class Negate : IValueConverter
{
    public static readonly Negate Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class NotNull : IValueConverter
{
    public static readonly NotNull Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value != null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
