using System.Collections;
using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Data.SqlClient;

namespace Beacon.Tests.Common;

/// <summary>
/// SQL Server exceptions for the tests of Core's reflective <c>SqlException</c> reader: a real one built the way the driver
/// builds it, and stand-ins emitted into a dynamic assembly named like SqlClient's, with its exact type names, whose members
/// Core cannot bind or read; plus a readable lookalike in an assembly of another name.
/// </summary>
internal static class TestSqlServerExceptions
{
    private const string SqlExceptionTypeName = "Microsoft.Data.SqlClient.SqlException";

    private const string SqlErrorTypeName = "Microsoft.Data.SqlClient.SqlError";

    // SqlException has no public constructor: build one the way the driver does, through its internal factory.
    public static SqlException Real(int number, string message, byte errorClass = 16, byte state = 1)
    {
        var errorConstructor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(x => x.GetParameters().Length == 8 && x.GetParameters()[0].ParameterType == typeof(int));
        var error = (SqlError)errorConstructor.Invoke([number, state, errorClass, "server", message, "procedure", 0, null]);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errors, [error]);
        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(x => x.Name == "CreateException"
                && x.GetParameters().Length == 2
                && x.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));

        return (SqlException)create.Invoke(null, [errors, "16.0"])!;
    }

    /// <summary>
    /// A <see cref="DbException"/> named <c>Microsoft.Data.SqlClient.SqlException</c> with no <c>Number</c> or
    /// <c>Errors</c>, so no member binds, and a SQLSTATE that another check would otherwise classify.
    /// </summary>
    public static Exception Unbindable(string message, string? sqlState = null)
    {
        return Emit(new Shape { NumberShape = NumberShape.None, ErrorsShape = ErrorsShape.None, SqlState = sqlState }, message);
    }

    /// <summary>
    /// A <see cref="DbException"/> named <c>Microsoft.Data.SqlClient.SqlException</c> whose <c>Number</c> reads
    /// <paramref name="number"/> but whose <c>Errors</c> getter throws, so the reader cannot finish.
    /// </summary>
    public static Exception ErrorsThrow(string message, int number, string? sqlState = null)
    {
        return Emit(new Shape { Number = number, ErrorsShape = ErrorsShape.Throws, SqlState = sqlState }, message);
    }

    /// <summary>A stand-in with SqlClient's type names and a <c>Number</c> and errors that are readable except as <paramref name="unreadable"/>.</summary>
    public static Exception Unreadable(UnreadableShape unreadable, string message, int number = 2627)
    {
        var shape = new Shape { Number = number, ErrorsShape = ErrorsShape.Elements };

        switch (unreadable)
        {
            case UnreadableShape.NumberNotInt:
                shape.NumberShape = NumberShape.NotInt;
                break;
            case UnreadableShape.NumberAmbiguous:
                shape.NumberShape = NumberShape.Ambiguous;
                break;
            case UnreadableShape.ErrorsNotEnumerable:
                shape.ErrorsShape = ErrorsShape.NotEnumerable;
                break;
            case UnreadableShape.NullError:
                shape.ElementShape = ElementShape.Null;
                break;
            case UnreadableShape.ErrorOfWrongType:
                shape.ElementShape = ElementShape.WrongType;
                break;
            case UnreadableShape.ErrorClassNotByte:
                shape.ElementShape = ElementShape.ClassNotByte;
                break;
            case UnreadableShape.ErrorStateNotByte:
                shape.ElementShape = ElementShape.StateNotByte;
                break;
            case UnreadableShape.ErrorGetterThrows:
                shape.ElementShape = ElementShape.GetterThrows;
                break;
            case UnreadableShape.SqlErrorTypeMissing:
                shape.DefineErrorType = false;
                break;
        }

        return Emit(shape, message);
    }

    /// <summary>
    /// A fully readable <c>Microsoft.Data.SqlClient.SqlException</c> (number and one error) in a dynamic assembly that is not
    /// SqlClient's: it must not be taken for the driver's exception.
    /// </summary>
    public static Exception Lookalike(int number, string message)
    {
        return Emit(new Shape { AssemblyName = "NotSqlClient", Number = number, ErrorsShape = ErrorsShape.Elements }, message);
    }

    private static Exception Emit(Shape shape, string message)
    {
        var name = new AssemblyName(shape.AssemblyName);
        var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run).DefineDynamicModule(shape.AssemblyName);

        var errorType = shape.DefineErrorType ? EmitErrorType(module, shape) : null;
        var errorInstance = errorType == null ? null : Activator.CreateInstance(errorType);

        Type? baseType = null;
        if (shape.NumberShape == NumberShape.Ambiguous)
        {
            baseType = EmitBase(module);
        }

        var type = module.DefineType(SqlExceptionTypeName, TypeAttributes.Public | TypeAttributes.Sealed, baseType ?? typeof(DbException));

        var baseConstructor = baseType == null
            ? typeof(DbException).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!
            : baseType.GetConstructor([typeof(string)])!;
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(string)]);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldarg_1);
        constructorIl.Emit(OpCodes.Call, baseConstructor);
        constructorIl.Emit(OpCodes.Ret);

        if (shape.SqlState != null)
        {
            var getSqlState = Getter(type, "get_SqlState", typeof(string), virtualOverride: true);
            var il = getSqlState.GetILGenerator();
            il.Emit(OpCodes.Ldstr, shape.SqlState);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(getSqlState, typeof(DbException).GetProperty(nameof(DbException.SqlState))!.GetMethod!);
        }

        switch (shape.NumberShape)
        {
            case NumberShape.Int:
                DefineProperty(type, "Number", typeof(int), il => il.Emit(OpCodes.Ldc_I4, shape.Number));
                break;
            case NumberShape.NotInt:
                DefineProperty(type, "Number", typeof(string), il => il.Emit(OpCodes.Ldstr, shape.Number.ToString()));
                break;
            case NumberShape.Ambiguous:
                // The base type's int Number and this long one: GetProperty("Number") finds two and throws.
                DefineProperty(type, "Number", typeof(long), il => il.Emit(OpCodes.Ldc_I8, (long)shape.Number));
                break;
        }

        FieldBuilder? errorsField = null;
        switch (shape.ErrorsShape)
        {
            case ErrorsShape.Throws:
                DefineProperty(type, "Errors", typeof(IEnumerable), Throw);
                break;
            case ErrorsShape.NotEnumerable:
                DefineProperty(type, "Errors", typeof(int), il => il.Emit(OpCodes.Ldc_I4_0));
                break;
            case ErrorsShape.Elements:
                errorsField = type.DefineField("errors", typeof(object[]), FieldAttributes.Public);
                DefineProperty(type, "Errors", typeof(object[]), il =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, errorsField);
                });
                break;
        }

        var created = type.CreateType();
        var exception = (Exception)Activator.CreateInstance(created, message)!;
        if (errorsField != null)
        {
            created.GetField("errors")!.SetValue(exception, new[] { ElementOf(shape.ElementShape, errorInstance) });
        }

        return exception;
    }

    private static object? ElementOf(ElementShape element, object? error)
    {
        return element switch
        {
            ElementShape.Null => null,
            ElementShape.WrongType => new object(),
            _ => error
        };
    }

    private static Type EmitErrorType(ModuleBuilder module, Shape shape)
    {
        var type = module.DefineType(SqlErrorTypeName, TypeAttributes.Public | TypeAttributes.Sealed, typeof(object));
        type.DefineDefaultConstructor(MethodAttributes.Public);

        DefineProperty(type, "Number", typeof(int), il => il.Emit(OpCodes.Ldc_I4, shape.Number));
        switch (shape.ElementShape)
        {
            case ElementShape.ClassNotByte:
                DefineProperty(type, "Class", typeof(int), il => il.Emit(OpCodes.Ldc_I4, 16));
                break;
            case ElementShape.GetterThrows:
                DefineProperty(type, "Class", typeof(byte), Throw);
                break;
            default:
                DefineProperty(type, "Class", typeof(byte), il => il.Emit(OpCodes.Ldc_I4, 16));
                break;
        }

        if (shape.ElementShape == ElementShape.StateNotByte)
        {
            DefineProperty(type, "State", typeof(int), il => il.Emit(OpCodes.Ldc_I4_1));
        }
        else
        {
            DefineProperty(type, "State", typeof(byte), il => il.Emit(OpCodes.Ldc_I4_1));
        }

        return type.CreateType();
    }

    private static Type EmitBase(ModuleBuilder module)
    {
        var type = module.DefineType("Microsoft.Data.SqlClient.SqlExceptionBase", TypeAttributes.Public, typeof(DbException));
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(string)]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(DbException).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!);
        il.Emit(OpCodes.Ret);
        DefineProperty(type, "Number", typeof(int), x => x.Emit(OpCodes.Ldc_I4, 1));

        return type.CreateType();
    }

    private static void Throw(ILGenerator il)
    {
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
    }

    // A public instance property whose getter runs the emitted body and returns (the body leaves the value or throws).
    private static void DefineProperty(TypeBuilder type, string name, Type propertyType, Action<ILGenerator> body)
    {
        var getter = Getter(type, $"get_{name}", propertyType, virtualOverride: false);
        var il = getter.GetILGenerator();
        body(il);
        il.Emit(OpCodes.Ret);
        type.DefineProperty(name, PropertyAttributes.None, propertyType, null).SetGetMethod(getter);
    }

    private static MethodBuilder Getter(TypeBuilder type, string name, Type returnType, bool virtualOverride)
    {
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        if (virtualOverride)
        {
            attributes |= MethodAttributes.Virtual;
        }

        return type.DefineMethod(name, attributes, returnType, Type.EmptyTypes);
    }

    private sealed class Shape
    {
        public string AssemblyName { get; init; } = "Microsoft.Data.SqlClient";

        public string? SqlState { get; init; }

        public int Number { get; init; }

        public NumberShape NumberShape { get; set; } = NumberShape.Int;

        public ErrorsShape ErrorsShape { get; set; }

        public ElementShape ElementShape { get; set; }

        public bool DefineErrorType { get; set; } = true;
    }

    private enum NumberShape
    {
        None,
        Int,
        NotInt,
        Ambiguous
    }

    private enum ErrorsShape
    {
        None,
        Throws,
        NotEnumerable,
        Elements
    }

    private enum ElementShape
    {
        Good,
        Null,
        WrongType,
        ClassNotByte,
        StateNotByte,
        GetterThrows
    }
}

/// <summary>How a SqlClient-named stand-in made by <see cref="TestSqlServerExceptions.Unreadable"/> cannot be read.</summary>
public enum UnreadableShape
{
    NumberNotInt,
    NumberAmbiguous,
    ErrorsNotEnumerable,
    NullError,
    ErrorOfWrongType,
    ErrorClassNotByte,
    ErrorStateNotByte,
    ErrorGetterThrows,
    SqlErrorTypeMissing
}
