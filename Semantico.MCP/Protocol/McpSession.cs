using System.Text.Json;
using System.Threading.Channels;

namespace Semantico.MCP.Protocol;

/// <summary>
/// Represents an active MCP client session over SSE.
/// </summary>
internal sealed class McpClientSession : IAsyncDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public int? UserId { get; set; }
    public int? ApiKeyId { get; set; }
    public List<int>? AllowedProjectIds { get; set; }
    public int? ActiveProjectId { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public bool IsInitialized { get; set; }

    private readonly Channel<string> _messageChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public ChannelReader<string> Messages => _messageChannel.Reader;

    public async Task SendEventAsync(string eventType, object data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        var sseMessage = $"event: {eventType}\ndata: {json}\n\n";
        await _messageChannel.Writer.WriteAsync(sseMessage, ct);
        LastActivityAt = DateTime.UtcNow;
    }

    public async Task SendMessageAsync(JsonRpcResponse response, CancellationToken ct = default)
    {
        await SendEventAsync("message", response, ct);
    }

    public ValueTask DisposeAsync()
    {
        _messageChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
