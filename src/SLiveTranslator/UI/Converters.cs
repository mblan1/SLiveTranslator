using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SLiveTranslator.UI;

/// <summary>
/// Visible for true, a non-empty text, a non-empty list or any other object; collapsed for false, "", an empty list
/// and null. The parameter "not" turns it round.
/// </summary>
public sealed class VisibleWhen : IValueConverter
{
    public static VisibleWhen Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool shown = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "not") shown = !shown;
        return shown ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Turns a bool round, for IsEnabled and IsReadOnly bindings.</summary>
public sealed class Not : IValueConverter
{
    public static Not Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>The info bar's severity for a <see cref="MessageKind"/>; the view models stay free of WPF-UI.</summary>
public sealed class SeverityOf : IValueConverter
{
    public static SeverityOf Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MessageKind.Success => InfoBarSeverity.Success,
        MessageKind.Warning => InfoBarSeverity.Warning,
        MessageKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
