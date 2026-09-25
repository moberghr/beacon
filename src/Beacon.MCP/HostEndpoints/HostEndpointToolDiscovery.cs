using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Beacon.Core.HostData;
using Beacon.Core.HostEndpoints;

namespace Beacon.MCP.HostEndpoints;

/// <summary>
/// Turns the host's endpoints that carry <see cref="IBeaconToolMetadata"/> into tool descriptors: name and read-only
/// checks, the HTTP method, one MCP argument per bound value (from ApiExplorer when it describes the endpoint, else
/// from the action's parameters) and the input JSON schema. Every problem is collected and thrown together, so a
/// misconfigured host fails startup with the whole list.
/// </summary>
internal sealed partial class HostEndpointToolDiscovery(
    IXmlDocumentationProvider documentation,
    IServiceProviderIsService? serviceCheck)
{
    private const int MaxFlattenDepth = 3;

    private static readonly JsonSerializerOptions SchemaOptions = CreateSchemaOptions();

    private static readonly IReadOnlySet<string> ForbiddenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Host",
        "Content-Type",
        "Content-Length"
    };

    private static readonly IReadOnlySet<Type> SpecialTypes = new HashSet<Type>
    {
        typeof(CancellationToken),
        typeof(HttpContext),
        typeof(HttpRequest),
        typeof(HttpResponse),
        typeof(ClaimsPrincipal),
        typeof(Stream),
        typeof(PipeReader)
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{2,47}$")]
    private static partial Regex NamePattern();

    public IReadOnlyList<HostEndpointToolDescriptor> Discover(
        IEnumerable<Endpoint> endpoints,
        IReadOnlyList<ApiDescription> apiDescriptions,
        bool hasFallbackPolicy)
    {
        var failures = new List<string>();
        var tools = new List<HostEndpointToolDescriptor>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            var metadata = endpoint.Metadata.GetMetadata<IBeaconToolMetadata>();
            if (metadata == null)
            {
                continue;
            }

            var display = endpoint.DisplayName ?? "(unnamed endpoint)";
            var endpointFailures = new List<string>();

            if (!NamePattern().IsMatch(metadata.Name ?? string.Empty))
            {
                endpointFailures.Add($"tool name '{metadata.Name}' must match ^[a-z][a-z0-9_]{{2,47}}$");
            }
            else if (owners.TryGetValue(metadata.Name, out var owner))
            {
                endpointFailures.Add($"tool name '{metadata.Name}' is already used by {owner}");
            }
            else
            {
                owners[metadata.Name] = display;
            }

            if (!metadata.IsReadOnlyDeclared || !metadata.ReadOnly)
            {
                endpointFailures.Add("ReadOnly must be declared true — only read-only endpoints can be exposed, and Beacon never infers it from the HTTP verb");
            }

            if (!HasAuthorizationDecision(endpoint) && !hasFallbackPolicy)
            {
                endpointFailures.Add("the endpoint declares no authorization ([Authorize], a policy, or [AllowAnonymous]) and the host has no fallback authorization policy; Beacon refuses to expose it");
            }

            if (endpoint is not RouteEndpoint routeEndpoint)
            {
                endpointFailures.Add("only route endpoints can be exposed");
            }
            else if (endpointFailures.Count == 0)
            {
                try
                {
                    tools.Add(Describe(routeEndpoint, metadata, apiDescriptions));
                }
                catch (HostEndpointToolDefinitionException ex)
                {
                    endpointFailures.Add(ex.Message);
                }
            }

            failures.AddRange(endpointFailures.Select(x => $"{display} [{metadata.Name}]: {x}"));
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Beacon host endpoint tools are misconfigured:\n- " + string.Join("\n- ", failures));
        }

        return tools
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static bool HasAuthorizationDecision(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null
        || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
        || endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>().Count > 0
        || endpoint.Metadata.GetOrderedMetadata<IAuthorizationRequirementData>().Count > 0;

    private HostEndpointToolDescriptor Describe(
        RouteEndpoint endpoint,
        IBeaconToolMetadata metadata,
        IReadOnlyList<ApiDescription> apiDescriptions)
    {
        var httpMethod = ChooseHttpMethod(endpoint);
        var method = ResolveMethod(endpoint);
        var methodDocs = method == null ? null : documentation.GetMethodDocumentation(method);

        var apiDescription = FindApiDescription(endpoint, metadata, apiDescriptions, httpMethod);
        var parameters = apiDescription != null
            ? FromApiDescription(endpoint, apiDescription, httpMethod, methodDocs)
            : FromSignature(endpoint, httpMethod, methodDocs);

        var duplicate = parameters
            .GroupBy(x => x.ArgumentName, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .FirstOrDefault();
        if (duplicate != null)
        {
            throw new HostEndpointToolDefinitionException($"two bound values share the argument name '{duplicate.Key}'");
        }

        if (parameters.Any(x => x.Source == HostEndpointParameterSource.Body)
            && parameters.Any(x => x.Source == HostEndpointParameterSource.Form))
        {
            throw new HostEndpointToolDefinitionException("an endpoint cannot bind both a JSON body and form fields");
        }

        if (parameters.Any(x => x.Source is HostEndpointParameterSource.Body or HostEndpointParameterSource.Form)
            && !HostEndpointToolDescriptor.HasBody(httpMethod))
        {
            throw new HostEndpointToolDefinitionException($"{httpMethod} cannot carry the body or form values the endpoint binds");
        }

        var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
        var summary = FirstNonEmpty(
            metadata.Description,
            methodDocs?.Summary,
            endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary,
            endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description)
            ?? $"Calls {httpMethod} {route} on the host application.";

        var description = $"{summary.Trim()}\n\nRead-only host endpoint {httpMethod} {route}, run with your host permissions.";

        return new HostEndpointToolDescriptor(
            metadata.Name,
            HostEndpointToolDescriptor.ToolNamePrefix + metadata.Name,
            description,
            httpMethod,
            endpoint,
            parameters,
            BuildInputSchema(parameters));
    }

    private static string ChooseHttpMethod(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        if (methods.Count == 0)
        {
            return HttpMethods.Get;
        }

        return methods
            .Where(HttpMethods.IsGet)
            .FirstOrDefault() ?? methods[0].ToUpperInvariant();
    }

    private static MethodInfo? ResolveMethod(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.MethodInfo
        ?? endpoint.Metadata.GetMetadata<MethodInfo>();

    private static ApiDescription? FindApiDescription(
        Endpoint endpoint,
        IBeaconToolMetadata metadata,
        IReadOnlyList<ApiDescription> apiDescriptions,
        string httpMethod)
    {
        var action = endpoint.Metadata.GetMetadata<ActionDescriptor>();

        var matches = apiDescriptions
            .Where(x => action != null
                ? ReferenceEquals(x.ActionDescriptor, action)
                : x.ActionDescriptor.EndpointMetadata.Any(y => ReferenceEquals(y, metadata)))
            .ToList();

        return matches
            .Where(x => string.Equals(x.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault() ?? matches.FirstOrDefault();
    }

    private List<HostEndpointToolParameter> FromApiDescription(
        RouteEndpoint endpoint,
        ApiDescription apiDescription,
        string httpMethod,
        XmlMethodDocumentation? methodDocs)
    {
        var parameters = new List<HostEndpointToolParameter>();
        var routeParameters = RouteParameterNames(endpoint);

        foreach (var description in apiDescription.ParameterDescriptions)
        {
            var type = description.Type ?? description.ModelMetadata?.ModelType ?? typeof(string);
            var source = description.Source;

            if (source == BindingSource.Services || source == BindingSource.Special || SpecialTypes.Contains(type))
            {
                continue;
            }

            if (source == BindingSource.FormFile || IsFileType(type))
            {
                throw new HostEndpointToolDefinitionException($"parameter '{description.Name}' binds a file upload, which tools cannot send");
            }

            var parameterName = description.ParameterDescriptor?.Name;
            var isTopLevel = parameterName != null && string.Equals(parameterName, description.Name, StringComparison.OrdinalIgnoreCase);
            var text = FirstNonEmpty(
                description.ModelMetadata?.Description,
                isTopLevel ? DescribeParameterFromDocs(methodDocs, parameterName!) : null,
                description.ModelMetadata is { MetadataKind: ModelMetadataKind.Property, ContainerType: { } container, PropertyName: { } property }
                    ? documentation.GetPropertySummary(container, property)
                    : null);

            var required = description.IsRequired || (routeParameters.TryGetValue(description.Name, out var optional) && !optional);

            if (source == BindingSource.Body)
            {
                parameters.Add(Parameter(description.Name, description.Name, HostEndpointParameterSource.Body, HostEndpointParameterShape.Json, required, text, type));
                continue;
            }

            if (source == BindingSource.Custom || (description.BindingInfo?.BinderType != null && !IsSimpleOrCollection(type)))
            {
                parameters.Add(RawFields(description.Name, HostEndpointToolDescriptor.HasBody(httpMethod) ? HostEndpointParameterSource.Form : HostEndpointParameterSource.Query, required, text));
                continue;
            }

            var target = source == BindingSource.Path ? HostEndpointParameterSource.Route
                : source == BindingSource.Query ? HostEndpointParameterSource.Query
                : source == BindingSource.Header ? HostEndpointParameterSource.Header
                : source == BindingSource.Form ? HostEndpointParameterSource.Form
                : routeParameters.ContainsKey(description.Name) ? HostEndpointParameterSource.Route
                : HostEndpointToolDescriptor.HasBody(httpMethod) ? HostEndpointParameterSource.Form
                : HostEndpointParameterSource.Query;

            if (!IsSimpleOrCollection(type))
            {
                parameters.AddRange(Flatten(type, string.Empty, target, 0));
                continue;
            }

            parameters.Add(Simple(description.Name, description.Name, target, type, required, text));
        }

        return parameters;
    }

    private List<HostEndpointToolParameter> FromSignature(RouteEndpoint endpoint, string httpMethod, XmlMethodDocumentation? methodDocs)
    {
        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action != null)
        {
            return FromControllerAction(endpoint, action, httpMethod, methodDocs);
        }

        var method = endpoint.Metadata.GetMetadata<MethodInfo>();
        if (method == null)
        {
            return [];
        }

        return FromDelegate(endpoint, method, httpMethod, methodDocs);
    }

    private List<HostEndpointToolParameter> FromControllerAction(
        RouteEndpoint endpoint,
        ControllerActionDescriptor action,
        string httpMethod,
        XmlMethodDocumentation? methodDocs)
    {
        var parameters = new List<HostEndpointToolParameter>();
        var routeParameters = RouteParameterNames(endpoint);

        foreach (var descriptor in action.Parameters)
        {
            var type = descriptor.ParameterType;
            var bindingInfo = descriptor.BindingInfo;
            var source = bindingInfo?.BindingSource;
            var parameterInfo = (descriptor as ControllerParameterDescriptor)?.ParameterInfo;

            if (source == BindingSource.Services || source == BindingSource.Special || SpecialTypes.Contains(type))
            {
                continue;
            }

            if (source == BindingSource.FormFile || IsFileType(type))
            {
                throw new HostEndpointToolDefinitionException($"parameter '{descriptor.Name}' binds a file upload, which tools cannot send");
            }

            var httpName = bindingInfo?.BinderModelName ?? descriptor.Name;
            var text = FirstNonEmpty(DescribeParameterFromDocs(methodDocs, descriptor.Name), DescriptionOf(parameterInfo));
            var required = HasRequiredAttribute(parameterInfo) || (routeParameters.TryGetValue(httpName, out var optional) && !optional);

            if (source == BindingSource.Body)
            {
                parameters.Add(Parameter(descriptor.Name, descriptor.Name, HostEndpointParameterSource.Body, HostEndpointParameterShape.Json, required, text, type));
                continue;
            }

            if (source == BindingSource.Header)
            {
                parameters.Add(Simple(descriptor.Name, httpName, HostEndpointParameterSource.Header, type, required, text));
                continue;
            }

            var carrier = HostEndpointToolDescriptor.HasBody(httpMethod) ? HostEndpointParameterSource.Form : HostEndpointParameterSource.Query;

            if (bindingInfo?.BinderType != null || source == BindingSource.Custom)
            {
                if (IsSimpleOrCollection(type))
                {
                    parameters.Add(Simple(descriptor.Name, httpName, carrier, type, required, text));
                }
                else
                {
                    parameters.Add(RawFields(descriptor.Name, carrier, required, text));
                }

                continue;
            }

            var target = source == BindingSource.Path ? HostEndpointParameterSource.Route
                : source == BindingSource.Query ? HostEndpointParameterSource.Query
                : source == BindingSource.Form ? HostEndpointParameterSource.Form
                : routeParameters.ContainsKey(httpName) ? HostEndpointParameterSource.Route
                : carrier;

            if (!IsSimpleOrCollection(type))
            {
                // MVC binds a top-level model from unprefixed keys unless the binding names a prefix explicitly.
                var prefix = bindingInfo?.BinderModelName is { Length: > 0 } explicitName ? explicitName + "." : string.Empty;
                parameters.AddRange(Flatten(type, prefix, target, 0));
                continue;
            }

            parameters.Add(Simple(descriptor.Name, httpName, target, type, required, text));
        }

        return parameters;
    }

    private List<HostEndpointToolParameter> FromDelegate(
        RouteEndpoint endpoint,
        MethodInfo method,
        string httpMethod,
        XmlMethodDocumentation? methodDocs)
    {
        var parameters = new List<HostEndpointToolParameter>();
        var routeParameters = RouteParameterNames(endpoint);
        var nullability = new NullabilityInfoContext();

        foreach (var parameterInfo in method.GetParameters())
        {
            var type = parameterInfo.ParameterType;
            var attributes = parameterInfo.GetCustomAttributes(true);
            var name = parameterInfo.Name ?? string.Empty;

            if (attributes.OfType<AsParametersAttribute>().Any())
            {
                throw new HostEndpointToolDefinitionException($"parameter '{name}' uses [AsParameters], which tools do not support; bind its members individually");
            }

            if (SpecialTypes.Contains(type)
                || attributes.OfType<IFromServiceMetadata>().Any()
                || attributes.OfType<FromKeyedServicesAttribute>().Any()
                || (serviceCheck?.IsService(type) ?? false))
            {
                continue;
            }

            if (IsFileType(type))
            {
                throw new HostEndpointToolDefinitionException($"parameter '{name}' binds a file upload, which tools cannot send");
            }

            var text = FirstNonEmpty(DescribeParameterFromDocs(methodDocs, name), DescriptionOf(parameterInfo));
            var required = HasRequiredAttribute(parameterInfo) || IsRequiredByDelegateRules(parameterInfo, nullability);

            if (attributes.OfType<IFromBodyMetadata>().Any())
            {
                parameters.Add(Parameter(name, name, HostEndpointParameterSource.Body, HostEndpointParameterShape.Json, required, text, type));
                continue;
            }

            if (attributes.OfType<IFromHeaderMetadata>().FirstOrDefault() is { } header)
            {
                parameters.Add(Simple(name, header.Name ?? name, HostEndpointParameterSource.Header, type, required, text));
                continue;
            }

            if (attributes.OfType<IFromRouteMetadata>().FirstOrDefault() is { } route)
            {
                parameters.Add(Simple(name, route.Name ?? name, HostEndpointParameterSource.Route, type, true, text));
                continue;
            }

            if (attributes.OfType<IFromQueryMetadata>().FirstOrDefault() is { } query)
            {
                parameters.Add(Simple(name, query.Name ?? name, HostEndpointParameterSource.Query, type, required, text));
                continue;
            }

            if (attributes.OfType<IFromFormMetadata>().FirstOrDefault() is { } form)
            {
                if (IsSimpleOrCollection(type))
                {
                    parameters.Add(Simple(name, form.Name ?? name, HostEndpointParameterSource.Form, type, required, text));
                }
                else
                {
                    var prefix = form.Name is { Length: > 0 } explicitName ? explicitName + "." : string.Empty;
                    parameters.AddRange(Flatten(type, prefix, HostEndpointParameterSource.Form, 0));
                }

                continue;
            }

            if (routeParameters.TryGetValue(name, out var optional))
            {
                parameters.Add(Simple(name, name, HostEndpointParameterSource.Route, type, !optional, text));
                continue;
            }

            if (IsSimpleOrCollection(type))
            {
                parameters.Add(Simple(name, name, HostEndpointParameterSource.Query, type, required, text));
                continue;
            }

            if (!HostEndpointToolDescriptor.HasBody(httpMethod))
            {
                throw new HostEndpointToolDefinitionException($"parameter '{name}' is a complex type inferred as a JSON body, which {httpMethod} cannot carry");
            }

            parameters.Add(Parameter(name, name, HostEndpointParameterSource.Body, HostEndpointParameterShape.Json, required, text, type));
        }

        return parameters;
    }

    private IEnumerable<HostEndpointToolParameter> Flatten(Type type, string prefix, HostEndpointParameterSource source, int depth)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanWrite)
            .Where(x => x.GetIndexParameters().Length == 0)
            .Where(x => x.GetCustomAttribute<BindNeverAttribute>() == null)
            .OrderBy(x => x.MetadataToken);

        foreach (var property in properties)
        {
            var key = prefix + (property.GetCustomAttributes(true).OfType<IModelNameProvider>().FirstOrDefault()?.Name ?? property.Name);
            var text = FirstNonEmpty(DescriptionOf(property), documentation.GetPropertySummary(property.DeclaringType!, property.Name));
            var required = property.GetCustomAttribute<RequiredAttribute>() != null || property.GetCustomAttribute<BindRequiredAttribute>() != null;

            if (IsSimpleOrCollection(property.PropertyType))
            {
                yield return Simple(key, key, source, property.PropertyType, required, text);
                continue;
            }

            if (depth + 1 >= MaxFlattenDepth || CollectionElement(property.PropertyType) != null || IsFileType(property.PropertyType))
            {
                // Collections of objects (Items[0].Name) and deep graphs are not flattened; tools leave them unset.
                continue;
            }

            foreach (var nested in Flatten(property.PropertyType, key + ".", source, depth + 1))
            {
                yield return nested;
            }
        }
    }

    private static HostEndpointToolParameter Simple(
        string argumentName,
        string httpName,
        HostEndpointParameterSource source,
        Type type,
        bool required,
        string? description)
    {
        if (source == HostEndpointParameterSource.Header && ForbiddenHeaders.Contains(httpName))
        {
            throw new HostEndpointToolDefinitionException($"header '{httpName}' cannot be a tool argument");
        }

        var shape = CollectionElement(type) != null ? HostEndpointParameterShape.Collection : HostEndpointParameterShape.Scalar;

        return Parameter(argumentName, httpName, source, shape, required, description, type);
    }

    private static HostEndpointToolParameter RawFields(string name, HostEndpointParameterSource source, bool required, string? description)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "number", "boolean")
            },
            ["description"] = FirstNonEmpty(description, "Bound by a custom model binder") + ". Each member is sent as its own " +
                (source == HostEndpointParameterSource.Form ? "form field" : "query value") + " (for example page, pageSize, sort, filter)."
        };

        return new HostEndpointToolParameter(name, name, source, HostEndpointParameterShape.RawFields, required, description, JsonSerializer.SerializeToElement(schema));
    }

    private static HostEndpointToolParameter Parameter(
        string argumentName,
        string httpName,
        HostEndpointParameterSource source,
        HostEndpointParameterShape shape,
        bool required,
        string? description,
        Type type)
    {
        var schema = SchemaFor(type);
        if (!string.IsNullOrWhiteSpace(description) && schema is JsonObject schemaObject)
        {
            schemaObject["description"] = description;
        }

        return new HostEndpointToolParameter(argumentName, httpName, source, shape, required, description, JsonSerializer.SerializeToElement(schema));
    }

    internal static JsonNode SchemaFor(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            // Model binding accepts enum names; the exporter would describe the numeric JSON form.
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames(underlying).Select(x => (JsonNode?)x).ToArray())
            };
        }

        try
        {
            return JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, type, new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = DescribeSchemaNode
            });
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new JsonObject();
        }
    }

    private static JsonNode DescribeSchemaNode(JsonSchemaExporterContext context, JsonNode schema)
    {
        var provider = context.PropertyInfo?.AttributeProvider;
        var description = provider?.GetCustomAttributes(typeof(DescriptionAttribute), true).OfType<DescriptionAttribute>().FirstOrDefault()?.Description;
        if (!string.IsNullOrWhiteSpace(description) && schema is JsonObject schemaObject)
        {
            schemaObject.Insert(0, "description", description);
        }

        return schema;
    }

    private static JsonElement BuildInputSchema(IReadOnlyList<HostEndpointToolParameter> parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
        {
            properties[parameter.ArgumentName] = JsonNode.Parse(parameter.Schema.GetRawText());
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        var required = parameters
            .Where(x => x.Required)
            .Select(x => (JsonNode?)x.ArgumentName)
            .ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required);
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    private static Dictionary<string, bool> RouteParameterNames(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.Parameters
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().IsOptional || x.First().Default != null, StringComparer.OrdinalIgnoreCase);

    internal static bool IsSimpleOrCollection(Type type) =>
        IsSimple(type) || (CollectionElement(type) is { } element && IsSimple(element));

    internal static bool IsSimple(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeOnly)
            || underlying == typeof(TimeSpan)
            || underlying == typeof(Guid)
            || underlying == typeof(Uri)
            || TypeDescriptor.GetConverter(underlying).CanConvertFrom(typeof(string));
    }

    internal static Type? CollectionElement(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type
            .GetInterfaces()
            .Append(type)
            .Where(x => x.IsGenericType)
            .Where(x => x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    private static bool IsFileType(Type type) =>
        typeof(IFormFile).IsAssignableFrom(type)
        || typeof(IFormFileCollection).IsAssignableFrom(type)
        || (CollectionElement(type) is { } element && typeof(IFormFile).IsAssignableFrom(element));

    private static bool HasRequiredAttribute(ICustomAttributeProvider? provider) =>
        provider != null
        && provider.GetCustomAttributes(true).Any(x => x is RequiredAttribute or BindRequiredAttribute);

    private static bool IsRequiredByDelegateRules(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        // Minimal APIs reject a missing value for a non-nullable parameter without a default.
        if (parameter.HasDefaultValue)
        {
            return false;
        }

        if (parameter.ParameterType.IsValueType)
        {
            return Nullable.GetUnderlyingType(parameter.ParameterType) == null;
        }

        return nullability.Create(parameter).WriteState == NullabilityState.NotNull;
    }

    private static string? DescribeParameterFromDocs(XmlMethodDocumentation? methodDocs, string parameterName) =>
        methodDocs != null && methodDocs.Parameters.TryGetValue(parameterName, out var text) ? text : null;

    private static string? DescriptionOf(ICustomAttributeProvider? provider) =>
        provider?.GetCustomAttributes(typeof(DescriptionAttribute), true).OfType<DescriptionAttribute>().FirstOrDefault()?.Description;

    private static string? FirstNonEmpty(params string?[] values) =>
        values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .FirstOrDefault();

    private static JsonSerializerOptions CreateSchemaOptions()
    {
        // Web naming (camelCase), but strict numbers: the lenient read-from-string handling would describe every
        // number as "string or integer" with a regex pattern, which only confuses agents.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly();

        return options;
    }
}

internal sealed class HostEndpointToolDefinitionException(string message) : Exception(message);
