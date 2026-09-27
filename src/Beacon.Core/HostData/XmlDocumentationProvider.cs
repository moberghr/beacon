using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostData;

/// <summary>
/// Supplies <c>&lt;summary&gt;</c> text from compiler-generated XML documentation for host entity types and
/// properties, and summary plus <c>&lt;param&gt;</c> text for host actions. Used to describe host-exposed tables and
/// columns when the EF model carries no <c>HasComment</c>, and host endpoint tools that set no description.
/// </summary>
public interface IXmlDocumentationProvider
{
    string? GetTypeSummary(Type type);

    string? GetPropertySummary(Type declaringType, string propertyName);

    /// <summary>The <c>&lt;summary&gt;</c> and <c>&lt;param&gt;</c> docs of a method (a host action), or null when it has none.</summary>
    XmlMethodDocumentation? GetMethodDocumentation(MethodInfo method) => null;
}

/// <param name="Summary">The method's summary, whitespace-collapsed.</param>
/// <param name="Parameters">Parameter name → its <c>&lt;param&gt;</c> text.</param>
public sealed record XmlMethodDocumentation(string? Summary, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Reads <c>&lt;assembly&gt;.xml</c> next to the entity assembly (enable <c>GenerateDocumentationFile</c> in the
/// host project). A missing or unreadable file yields no descriptions; only the assembly name is ever logged.
/// </summary>
internal sealed class AssemblyXmlDocumentationProvider(ILogger<AssemblyXmlDocumentationProvider> logger)
    : IXmlDocumentationProvider
{
    // Separates a member id from one of its <param> names in the parsed dictionary; never part of a member id.
    internal const string ParameterSeparator = "#param:";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>> _documents = new();

    public string? GetTypeSummary(Type type)
    {
        return Lookup(type.Assembly, $"T:{MemberTypeName(type)}");
    }

    public string? GetPropertySummary(Type declaringType, string propertyName)
    {
        return Lookup(declaringType.Assembly, $"P:{MemberTypeName(declaringType)}.{propertyName}");
    }

    public XmlMethodDocumentation? GetMethodDocumentation(MethodInfo method)
    {
        var declaringType = method.DeclaringType;
        if (declaringType == null)
        {
            return null;
        }

        var members = _documents.GetOrAdd(declaringType.Assembly, Load);
        var memberName = ResolveMethodMemberName(members, method);
        if (memberName == null)
        {
            return null;
        }

        members.TryGetValue(memberName, out var summary);

        var parameterPrefix = memberName + ParameterSeparator;
        var parameters = members
            .Where(x => x.Key.StartsWith(parameterPrefix, StringComparison.Ordinal))
            .ToDictionary(x => x.Key[parameterPrefix.Length..], x => x.Value, StringComparer.Ordinal);

        return summary == null && parameters.Count == 0 ? null : new XmlMethodDocumentation(summary, parameters);
    }

    /// <summary>
    /// The documentation member id of <paramref name="method"/>: the exact <c>M:Type.Method(Params)</c> id when it is
    /// documented, else the only documented overload of that name (the id encoding of exotic parameter types is not
    /// worth reproducing exactly).
    /// </summary>
    internal static string? ResolveMethodMemberName(IReadOnlyDictionary<string, string> members, MethodInfo method)
    {
        var exact = MethodMemberName(method);
        if (members.ContainsKey(exact) || members.Keys.Any(x => x.StartsWith(exact + ParameterSeparator, StringComparison.Ordinal)))
        {
            return exact;
        }

        var prefix = $"M:{MemberTypeName(method.DeclaringType!)}.{method.Name}";
        var candidates = members.Keys
            .Select(x => x.Split(ParameterSeparator)[0])
            .Where(x => x == prefix || x.StartsWith(prefix + "(", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    internal static string MethodMemberName(MethodInfo method)
    {
        var name = $"M:{MemberTypeName(method.DeclaringType!)}.{method.Name}";
        if (method.IsGenericMethodDefinition || method.IsGenericMethod)
        {
            name += "``" + method.GetGenericArguments().Length;
        }

        var parameters = method.GetParameters();

        return parameters.Length == 0
            ? name
            : name + "(" + string.Join(",", parameters.Select(x => DocumentationTypeId(x.ParameterType))) + ")";
    }

    private static string DocumentationTypeId(Type type)
    {
        if (type.IsByRef)
        {
            return DocumentationTypeId(type.GetElementType()!) + "@";
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            var suffix = rank == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", rank)) + "]";

            return DocumentationTypeId(type.GetElementType()!) + suffix;
        }

        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod != null ? "``" : "`") + type.GenericParameterPosition;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = definition.IndexOf('`');
            var baseName = (tick < 0 ? definition : definition[..tick]).Replace('+', '.');

            return baseName + "{" + string.Join(",", type.GetGenericArguments().Select(DocumentationTypeId)) + "}";
        }

        return MemberTypeName(type);
    }

    private string? Lookup(Assembly assembly, string memberName)
    {
        var members = _documents.GetOrAdd(assembly, Load);

        return members.TryGetValue(memberName, out var summary) ? summary : null;
    }

    private IReadOnlyDictionary<string, string> Load(Assembly assembly)
    {
        var empty = new Dictionary<string, string>();

        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
        {
            return empty;
        }

        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
        {
            return empty;
        }

        try
        {
            return Parse(XDocument.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            logger.LogWarning(
                "XML documentation for assembly {AssemblyName} could not be read ({ExceptionType}); host descriptions fall back to the EF model.",
                assembly.GetName().Name,
                ex.GetType().Name);

            return empty;
        }
    }

    internal static IReadOnlyDictionary<string, string> Parse(XDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var members = document.Root?
            .Element("members")?
            .Elements("member") ?? [];

        foreach (var member in members)
        {
            var name = member.Attribute("name")?.Value;
            if (name == null)
            {
                continue;
            }

            var summary = member.Element("summary");
            if (summary != null)
            {
                var text = Render(summary);
                if (text.Length > 0)
                {
                    result[name] = text;
                }
            }

            foreach (var parameter in member.Elements("param"))
            {
                var parameterName = parameter.Attribute("name")?.Value;
                var text = Render(parameter);
                if (!string.IsNullOrEmpty(parameterName) && text.Length > 0)
                {
                    result[name + ParameterSeparator + parameterName] = text;
                }
            }
        }

        return result;
    }

    private static string Render(XElement element)
    {
        return Whitespace.Replace(string.Concat(element.Nodes().Select(RenderNode)), " ").Trim();
    }

    private static string RenderNode(XNode node)
    {
        if (node is XText text)
        {
            return text.Value;
        }

        if (node is not XElement element)
        {
            return string.Empty;
        }

        // <see cref="T:Ns.Type"/> renders as the short member name; other inline tags render their text.
        var cref = element.Attribute("cref")?.Value ?? element.Attribute("langword")?.Value;
        if (cref != null && !element.Nodes().Any())
        {
            return cref[(cref.LastIndexOf('.') + 1)..];
        }

        return string.Concat(element.Nodes().Select(RenderNode));
    }

    private static string MemberTypeName(Type type)
    {
        return (type.FullName ?? type.Name).Replace('+', '.');
    }
}
