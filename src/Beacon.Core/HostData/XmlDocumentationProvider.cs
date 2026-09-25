using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostData;

/// <summary>
/// Supplies <c>&lt;summary&gt;</c> text from compiler-generated XML documentation for host entity types and
/// properties. Used to describe host-exposed tables and columns when the EF model carries no <c>HasComment</c>.
/// </summary>
public interface IXmlDocumentationProvider
{
    string? GetTypeSummary(Type type);

    string? GetPropertySummary(Type declaringType, string propertyName);
}

/// <summary>
/// Reads <c>&lt;assembly&gt;.xml</c> next to the entity assembly (enable <c>GenerateDocumentationFile</c> in the
/// host project). A missing or unreadable file yields no descriptions; only the assembly name is ever logged.
/// </summary>
internal sealed class AssemblyXmlDocumentationProvider(ILogger<AssemblyXmlDocumentationProvider> logger)
    : IXmlDocumentationProvider
{
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
            var summary = member.Element("summary");
            if (name == null || summary == null)
            {
                continue;
            }

            var text = Whitespace.Replace(string.Concat(summary.Nodes().Select(RenderNode)), " ").Trim();
            if (text.Length > 0)
            {
                result[name] = text;
            }
        }

        return result;
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
