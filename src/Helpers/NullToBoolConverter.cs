using System;
using System.Globalization;
using System.Windows.Data;

namespace CustomBrowser
{
    /// <summary>
    /// Returns true when the bound value is non-null, false when null.
    /// Used to show/hide the suggestion entity thumbnail based on whether CachedIcon was preloaded.
    /// </summary>
    [ValueConversion(typeof(object), typeof(bool))]
    public sealed class NullToBoolConverter : IValueConverter
    {
        public static readonly NullToBoolConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value != null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

