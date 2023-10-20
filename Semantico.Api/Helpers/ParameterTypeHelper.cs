using Semantico.Api.Data.Enums;
using Semantico.Api.Types;
using System.Globalization;

namespace Semantico.Api.Helpers;

public static class ParameterTypeHelper
{
    public static bool CanParseParameter(this string value, ParameterType type)
    {
        return value.ParseParameter(type) != null;
    }

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
                    return null!;
                }
            case ParameterType.DateTime:
                {
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsed))
                    {
                        return parsed;
                    }
                    return null!;
                }
            case ParameterType.String:
                {
                    return value;
                }
            default:
                throw new SemanticoException($"Unsupported parameter type");
        }
    }
}
