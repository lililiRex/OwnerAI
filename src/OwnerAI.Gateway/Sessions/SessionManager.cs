using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OwnerAI.Gateway.Sessions;

/// <summary>
/// 内存会话管理实现 — Phase 1 使用内存存储，后续切换到 SQLite
/// </summary>
public sealed class SessionManager(ILogger<SessionManager> logger) : ISessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _channelSenderToSession = new();

    public Task<SessionInfo> GetOrCreateSessionAsync(string channelId, string senderId, CancellationToken ct)
    {
        var key = $"{channelId}:{senderId}";

        if (_channelSenderToSession.TryGetValue(key, out var sessionId) &&
            _sessions.TryGetValue(sessionId, out var existing) &&
            existing.IsActive)
        {
            return Task.FromResult(existing);
        }

        var session = new SessionInfo
        {
            Id = Ulid.NewUlid().ToString(),
            ChannelId = channelId,
        };

        _sessions[session.Id] = session;
        _channelSenderToSession[key] = session.Id;

        logger.LogInformation("[Session] Created session {SessionId} for {Channel}/{Sender}",
            session.Id, channelId, senderId);

        return Task.FromResult(session);
    }

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task EndSessionAsync(string sessionId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.EndedAt = DateTimeOffset.Now;
            logger.LogInformation("[Session] Ended session {SessionId}, turns={Turns}",
                sessionId, session.TurnCount);
        }
        return Task.CompletedTask;
    }

    public Task IncrementTurnAsync(string sessionId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.TurnCount++;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionInfo>> GetActiveSessionsAsync(CancellationToken ct)
    {
        var active = _sessions.Values.Where(s => s.IsActive).ToList();
        return Task.FromResult<IReadOnlyList<SessionInfo>>(active);
    }
}
