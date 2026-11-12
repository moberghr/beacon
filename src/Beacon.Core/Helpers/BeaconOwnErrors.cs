using System.Reflection;
using Beacon.Core.Models;

namespace Beacon.Core.Helpers;

/// <summary>
/// Whether an exception's message is Beacon's own text, fit to reach a caller or a stored record as written: a
/// <see cref="BeaconException"/>, or an <see cref="InvalidOperationException"/> (exactly that type, or one Beacon declares)
/// thrown by Beacon's code. A library's exception of the same type carries the library's text — a driver's can quote row
/// values, hosts or settings — and is not Beacon's own; neither is an exception that was never thrown or whose throwing
/// method cannot be read (fail closed).
/// </summary>
internal static class BeaconOwnErrors
{
    /// <summary>True when <paramref name="exception"/>'s message is Beacon's own text (see the type).</summary>
    public static bool IsOwnMessage(Exception exception) =>
        exception switch
        {
            BeaconException => true,
            InvalidOperationException => IsThrownByBeacon(exception),
            _ => false
        };

    /// <summary>
    /// True when <paramref name="exception"/> is exactly an <see cref="InvalidOperationException"/> or a type a Beacon
    /// assembly declares, and the method that threw it is Beacon's (an async method's state machine and a lambda count as
    /// the Beacon type that holds them).
    /// </summary>
    public static bool IsThrownByBeacon(Exception exception)
    {
        var type = exception.GetType();
        if (type != typeof(InvalidOperationException) && !IsBeaconAssembly(type.Assembly))
        {
            return false;
        }

        MethodBase? site;
        try
        {
            site = exception.TargetSite;
        }
        catch (Exception)
        {
            return false;
        }

        return site?.DeclaringType?.Assembly is { IsDynamic: false } assembly && IsBeaconAssembly(assembly);
    }

    private static bool IsBeaconAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;

        return name != null
            && (name == "Beacon" || name.StartsWith("Beacon.", StringComparison.Ordinal));
    }
}
