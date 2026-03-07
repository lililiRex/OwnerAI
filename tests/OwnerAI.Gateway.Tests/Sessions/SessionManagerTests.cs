using Microsoft.Extensions.Logging.Abstractions;
using OwnerAI.Gateway.Sessions;

namespace OwnerAI.Gateway.Tests.Sessions;

public class SessionManagerTests
{
    private readonly SessionManager _sut = new(NullLogger<SessionManager>.Instance);

    [Fact]
    public async Task GetOrCreateSession_CreatesNewSession()
    {
        var session = await _sut.GetOrCreateSessionAsync("cli", "owner", CancellationToken.None);

        Assert.NotNull(session);
        Assert.NotEmpty(session.Id);
        Assert.Equal("cli", session.ChannelId);
        Assert.True(session.IsActive);
    }

    [Fact]
    public async Task GetOrCreateSession_ReturnsSameForSameKey()
    {
        var session1 = await _sut.GetOrCreateSessionAsync("cli", "owner", CancellationToken.None);
        var session2 = await _sut.GetOrCreateSessionAsync("cli", "owner", CancellationToken.None);

        Assert.Equal(session1.Id, session2.Id);
    }

    [Fact]
    public async Task GetOrCreateSession_DifferentKeys_DifferentSessions()
    {
        var session1 = await _sut.GetOrCreateSessionAsync("cli", "owner", CancellationToken.None);
        var session2 = await _sut.GetOrCreateSessionAsync("telegram", "user1", CancellationToken.None);

        Assert.NotEqual(session1.Id, session2.Id);
    }

    [Fact]
    public async Task EndSession_MarksInactive()
    {
        var session = await _sut.GetOrCreateSessionAsync("cli", "owner", CancellationToken.None);
        await _sut.EndSessionAsync(session.Id, CancellationToken.None);

        var found = await _sut.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.False(found.IsActive);
        Assert.NotNull(found.EndedAt);
    }

    [Fact]
    public async Task GetActiveSessions_ReturnsOnlyActive()
    {
        var s1 = await _sut.GetOrCreateSessionAsync("cli", "owner", CancellationToken.None);
        var s2 = await _sut.GetOrCreateSessionAsync("telegram", "user1", CancellationToken.None);
        await _sut.EndSessionAsync(s1.Id, CancellationToken.None);

        var active = await _sut.GetActiveSessionsAsync(CancellationToken.None);
        Assert.Single(active);
        Assert.Equal(s2.Id, active[0].Id);
    }
}
