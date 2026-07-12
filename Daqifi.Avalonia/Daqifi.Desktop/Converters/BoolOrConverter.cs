using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Daqifi.Desktop.Converters;

/// <summary>
/// OR-combines boolean bindings. Downstream-only: drives the single SplitView
/// flyout host's IsPaneOpen from the per-flyout open flags (hal: flyout_host —
/// MahApps gave each Flyout its own IsOpen; the one-host mechanism needs the
/// disjunction).
/// </summary>
public class BoolOrConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var value in values)
        {
            if (value is bool b && b)
            {
                return true;
            }
        }
        return false;
    }
}
