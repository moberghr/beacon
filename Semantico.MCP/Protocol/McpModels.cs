using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semantico.MCP.Protocol;

// JSON-RPC 2.0 models for MCP protocol

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

// MCP-specific models

public class McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "semantico";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}

public class McpCapabilities
{
    [JsonPropertyName("tools")]
    public McpToolsCapability? Tools { get; set; } = new();

    [JsonPropertyName("resources")]
    public McpResourcesCapability? Resources { get; set; } = new();
}

public class McpToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; } = false;
}

public class McpResourcesCapability
{
    [JsonPropertyName("subscribe")]
    public bool Subscribe { get; set; } = false;

    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; } = false;
}

public class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();
}

public class McpTool
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inputSchema")]
    public required object InputSchema { get; set; }
}

public class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

public class McpContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public class McpResource
{
    [JsonPropertyName("uri")]
    public required string Uri { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }
}

public class McpResourceContent
{
    [JsonPropertyName("uri")]
    public required string Uri { get; set; }

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "text/plain";

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public class McpListToolsResult
{
    [JsonPropertyName("tools")]
    public List<McpTool> Tools { get; set; } = new();
}

public class McpListResourcesResult
{
    [JsonPropertyName("resources")]
    public List<McpResource> Resources { get; set; } = new();
}

public class McpReadResourceResult
{
    [JsonPropertyName("contents")]
    public List<McpResourceContent> Contents { get; set; } = new();
}
