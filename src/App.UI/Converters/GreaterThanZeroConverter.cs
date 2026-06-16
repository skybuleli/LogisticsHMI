using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace App.UI.Converters;

/// <summary>
/// 将 int 值转换为 bool 值：大于 0 返回 true，否则返回 false。
/// 用于控制通知徽标等元素的可见性。
/// </summary>
public class GreaterThanZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue > 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// 单例实例，可在 XAML 中通过 {x:Static} 引用。
    /// </summary>
    public static readonly GreaterThanZeroConverter Instance = new();
}
