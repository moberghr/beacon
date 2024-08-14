using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using System.Globalization;

namespace Semantico.Core.Helpers;

public static class ParameterTypeHelper
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
                throw new SemanticoException($"Unsupported parameter type");
        }

        throw new SemanticoException($"Unable to parse value '{value}' as type {type}.");
    }
}
