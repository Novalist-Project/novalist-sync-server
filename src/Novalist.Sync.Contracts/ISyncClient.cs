namespace Novalist.Sync.Contracts;

public interface ISyncClient
{
    Task ProjectEpochChanged(Guid projectId, uint epoch);
    Task ProjectHistoryCompacted(Guid projectId, long historyStartsAt);
    Task ProjectAccessEnded(Guid projectId);
    Task ProjectDeviceAuthorized(Guid projectId);
    Task ReceiveUpdate(SequencedEnvelope envelope);
    Task ReceivePresence(Envelope envelope);
}
