using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelaySessions
{
    private readonly ConcurrentDictionary<Guid, RelaySession> sessions = new();
    private readonly RelayOptions options;

    public RelaySessions(IOptions<RelayOptions> configuration) => options = configuration.Value;

    internal RelaySession Create(RelayIdentity owner, byte[]? hostProof)
    {
        var session = new RelaySession(owner, hostProof, options);
        if (!sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Relay session collision.");
        return session;
    }

    internal RelaySession? Find(Guid id) => sessions.GetValueOrDefault(id);
    internal void Remove(RelaySession session)
    {
        sessions.TryRemove(session.Id, out _);
        session.Close();
    }
}
