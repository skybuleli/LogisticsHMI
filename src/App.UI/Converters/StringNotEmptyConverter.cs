using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace App.UI.Converters;

/// <summary>
/// 将字符串转换为 bool：非 null 且非空字符串返回 true，否则返回 false。
/// </summary>
public class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && s.Length > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// 单例实例，可在 XAML 中通过 {x:Static} 引用。
    /// </summary>
    public static readonly StringNotEmptyConverter Instance = new();
}
