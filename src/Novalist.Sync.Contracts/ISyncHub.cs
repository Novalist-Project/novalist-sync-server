namespace Novalist.Sync.Contracts;

public interface ISyncHub
{

    Task JoinProject(Guid projectId);
    Task LeaveProject(Guid projectId);

    Task<long> Push(Envelope envelope);

    Task CreateProject(Guid projectId);

    IAsyncEnumerable<SequencedEnvelope> CatchUp(
        Guid projectId,
        long afterServerSeq,
        CancellationToken cancellationToken);
}
