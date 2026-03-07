using System.Collections.Concurrent;

namespace Semantico.MCP.Protocol;

internal sealed class McpSessionManager
{
    private readonly ConcurrentDictionary<string, McpClientSession> _sessions = new();

    public McpClientSession CreateSession(int? userId, int? apiKeyId)
    {
        var session = new McpClientSession
        {
            UserId = userId,
            ApiKeyId = apiKeyId
        };
        _sessions.TryAdd(session.SessionId, session);
        return session;
    }

    public McpClientSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public async Task RemoveSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            await session.DisposeAsync();
    }

    public IReadOnlyCollection<McpClientSession> GetActiveSessions()
        => _sessions.Values.ToList().AsReadOnly();

    public int ActiveSessionCount => _sessions.Count;
}
