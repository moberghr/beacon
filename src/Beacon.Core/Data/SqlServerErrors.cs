using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Beacon.Core.Data;

/// <summary>One error a SQL Server exception carries: its number, severity (class) and state.</summary>
internal sealed record SqlServerError(int Number, byte Class, byte State);

/// <summary>
/// What <see cref="SqlServerErrors.Read"/> found in an exception: whether it is SqlClient's <c>SqlException</c> and, when
/// it is, whether its error number and errors could be read. An exception that is a <c>SqlException</c> but could not be
/// read is <see cref="Unreadable"/>: its callers then fail closed (no classification, no code, no message).
/// </summary>
internal readonly record struct SqlServerErrorInfo(bool IsSqlException, bool IsReadable, int Number, IReadOnlyList<SqlServerError> Errors)
{
    public static readonly SqlServerErrorInfo NotSqlException = new(false, false, 0, Array.Empty<SqlServerError>());

    public static readonly SqlServerErrorInfo Unreadable = new(true, false, 0, Array.Empty<SqlServerError>());
}

/// <summary>
/// Reads SQL Server's error number and errors from the SqlClient driver's <c>SqlException</c> by name, so reading them
/// does not reference the driver (Core still has one bulk-copy use of it until a later change): the exception is
/// recognised by its exact full type name in the <c>Microsoft.Data.SqlClient</c> assembly and its members are bound once
/// per type through reflection. A <c>SqlException</c> whose members cannot be bound or read is reported
/// <see cref="SqlServerErrorInfo.Unreadable"/>, never as an exception of another kind.
/// </summary>
internal static class SqlServerErrors
{
    private const string SqlExceptionTypeName = "Microsoft.Data.SqlClient.SqlException";

    private const string SqlClientAssemblyName = "Microsoft.Data.SqlClient";

    private const string SqlErrorTypeName = "Microsoft.Data.SqlClient.SqlError";

    private static readonly ConcurrentDictionary<Type, Members?> BoundMembers = new();

    public static SqlServerErrorInfo Read(Exception exception)
    {
        var type = exception.GetType();
        if (type.FullName != SqlExceptionTypeName || type.Assembly.GetName().Name != SqlClientAssemblyName)
        {
            return SqlServerErrorInfo.NotSqlException;
        }

        var members = BoundMembers.GetOrAdd(type, Bind);
        if (members == null)
        {
            return SqlServerErrorInfo.Unreadable;
        }

        try
        {
            if (members.Number.GetValue(exception) is not int number || members.Errors.GetValue(exception) is not IEnumerable errors)
            {
                return SqlServerErrorInfo.Unreadable;
            }

            var read = new List<SqlServerError>();
            foreach (var error in errors)
            {
                if (error?.GetType() != members.ErrorType
                    || members.ErrorNumber.GetValue(error) is not int errorNumber
                    || members.ErrorClass.GetValue(error) is not byte errorClass
                    || members.ErrorState.GetValue(error) is not byte errorState)
                {
                    return SqlServerErrorInfo.Unreadable;
                }

                read.Add(new SqlServerError(errorNumber, errorClass, errorState));
            }

            return new SqlServerErrorInfo(true, true, number, read);
        }
        catch (Exception)
        {
            // A getter that throws leaves the error unread: the callers fail closed on Unreadable.
            return SqlServerErrorInfo.Unreadable;
        }
    }

    private static Members? Bind(Type exceptionType)
    {
        try
        {
            return BindMembers(exceptionType);
        }
        catch (Exception)
        {
            // A member that cannot be bound (an ambiguous or unloadable one) leaves the type unreadable.
            return null;
        }
    }

    private static Members? BindMembers(Type exceptionType)
    {
        var errorType = exceptionType.Assembly.GetType(SqlErrorTypeName);
        var number = Property(exceptionType, "Number", typeof(int));
        var errors = exceptionType.GetProperty("Errors", BindingFlags.Public | BindingFlags.Instance);
        if (errorType == null || number == null || errors == null || !typeof(IEnumerable).IsAssignableFrom(errors.PropertyType))
        {
            return null;
        }

        var errorNumber = Property(errorType, "Number", typeof(int));
        var errorClass = Property(errorType, "Class", typeof(byte));
        var errorState = Property(errorType, "State", typeof(byte));

        return errorNumber == null || errorClass == null || errorState == null
            ? null
            : new Members(number, errors, errorType, errorNumber, errorClass, errorState);
    }

    private static PropertyInfo? Property(Type type, string name, Type propertyType)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        return property?.PropertyType == propertyType && property.GetMethod != null ? property : null;
    }

    private sealed record Members(
        PropertyInfo Number,
        PropertyInfo Errors,
        Type ErrorType,
        PropertyInfo ErrorNumber,
        PropertyInfo ErrorClass,
        PropertyInfo ErrorState);
}
