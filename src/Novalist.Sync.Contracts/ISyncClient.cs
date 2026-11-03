namespace Novalist.Sync.Contracts;

public interface ISyncClient
{
    Task ReceiveUpdate(SequencedEnvelope envelope);
}
