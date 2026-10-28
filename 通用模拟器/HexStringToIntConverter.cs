using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace 通用模拟器
{
    public class HexStringToIntConverter : ConverterBase
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int n)
                return n.ToString("X");
            return string.Empty;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
                return DependencyProperty.UnsetValue;

            if (int.TryParse(text, NumberStyles.HexNumber, null, out var n))
                return n;

            return DependencyProperty.UnsetValue;
        }
    }
}
