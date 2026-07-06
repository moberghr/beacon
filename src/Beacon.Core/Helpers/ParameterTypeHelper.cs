using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using System.Globalization;

namespace Beacon.Core.Helpers;

internal static class ParameterTypeHelper
{
    public static object ParseParameter(this string value, ParameterType type)
    {
        switch (type)
        {
            case ParameterType.Number:
                {
                    if (Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        return parsed;
                    }
                    break;
                }
            case ParameterType.DateTime:
                {
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsed))
                    {
                        return parsed;
                    }
                    break;
                }
            case ParameterType.String:
                {
                    return value;
                }
            default:
                throw new BeaconException($"Unsupported parameter type");
        }

        throw new BeaconException($"Unable to parse value '{value}' as type {type}.");
    }
}
