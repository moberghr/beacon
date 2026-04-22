using System.Collections;
using System.Reflection;

namespace Beacon.Core.Helpers;

internal static class ObjectHelpers
{
    public static object? GetPropertyValue(object? src, string propName, string? defaultValue)
    {
        return GetValues(src, propName, defaultValue);
    }

    public static object? GetPropertyValue(object? src, string propName)
    {
        return GetValues(src, propName, null);
    }

    private static object? GetValues(object? src, string propName, string? defaultValue)
    {
        ArgumentNullException.ThrowIfNull(src, nameof(src));
        ArgumentNullException.ThrowIfNull(propName, nameof(propName));

        if (propName.Contains('.'))
        {
            var temp = propName.Split('.');

            // For nullable types (e.g. ILookupResponse<T>?) if the src.<thatProp> is NULL, we will not find tempValue.
            // Then, there is no point to looking inside this NULL tempValue to find underlying value (e.g. Name prop in case of ILookupResponse)
            var tempValue = GetValues(src, temp[0], defaultValue);
            if (tempValue == null)
            {
                return defaultValue;
            }

            return GetValues(tempValue, temp[1], defaultValue);
        }

        var prop = src.GetType().GetProperty(propName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        var value = prop?.GetValue(src, null) ?? defaultValue;

        if (prop != null && value != null && prop.PropertyType == typeof(decimal))
        {
            value = Math.Round(Convert.ToDecimal(value), 2, MidpointRounding.ToEven).ToString("F2");
        }

        // Empty string converter. There is an issue when exporting CSV for empty strings. For XSLX we need to put null value (empty string can cause exception on datetime cells)
        if (value is string && string.IsNullOrEmpty((string)value))
        {
            return defaultValue;
        }

        // String list will be flattened into string separated with ; (we cannot use comma because it is delimiter sign for CSV)
        if (value is List<string> list)
        {
            if (!list.Any())
            {
                return defaultValue;
            }

            return string.Join("; ", list);
        }

        return value;
    }

    public static Type? GetPropertyType(Type? src, string propName)
    {
        ArgumentNullException.ThrowIfNull(src, nameof(src));
        ArgumentNullException.ThrowIfNull(propName, nameof(propName));

        if (propName.Contains('.'))
        {
            var temp = propName.Split('.');
            return GetPropertyType(GetPropertyType(src, temp[0]), temp[1]);
        }

        var prop = src.GetProperty(propName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        // list will be flattened into string
        if (typeof(IEnumerable).IsAssignableFrom(prop?.PropertyType))
        {
            return typeof(string);
        }

        return prop?.PropertyType;
    }

    public static List<string> GetPropertyNames(object obj)
    {
        if (obj == null)
        {
            return [];
        }

        var properties = obj.GetType().GetProperties(BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        return properties.Select(x => x.Name).ToList();
    }

    public static List<string> GetPropertyNames<T>()
    {
        var properties = typeof(T).GetProperties(BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        return properties.Select(p => p.Name).ToList();
    }
}