/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MediaPlayer.Views;

public class IsCurrentConverter : IValueConverter
{
    private static SolidColorBrush pressed = Application.Current.FindResource("PressedBrush") as SolidColorBrush;
    private static SolidColorBrush back = Application.Current.FindResource("BackgroundBrush") as SolidColorBrush;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value ? pressed : back;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
