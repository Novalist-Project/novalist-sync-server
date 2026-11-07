using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;

namespace Novalist.Sync.Server.Updates;

public sealed partial class UpdateLog
{
    public async Task EnsureHistoryAvailableAsync(Guid projectId, long afterServerSeq, CancellationToken cancellationToken)
    {
        var historyStartsAt = await db.Projects.AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => (long?)project.HistoryStartsAt).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AccessDeniedException("The shared project is unavailable.");
        if (afterServerSeq < historyStartsAt - 1)
            throw new ConflictException(
                $"This project's history before update {historyStartsAt} was replaced by a checkpoint. Refresh the project keys to continue.");
    }

    public async IAsyncEnumerable<SequencedEnvelope> ReadAfterAsync(
        Guid projectId, long afterServerSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var batch = await ReadHistoryPageAsync(projectId, afterServerSeq, cancellationToken);
            foreach (var update in batch)
            {
                afterServerSeq = update.ServerSeq;
                yield return update;
            }
            if (batch.Count < 128) yield break;
        }
    }

    private async Task<List<SequencedEnvelope>> ReadHistoryPageAsync(Guid projectId, long afterServerSeq, CancellationToken ct)
    {
        // Keep validation and the page on one snapshot, but release it before network delivery.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await EnsureHistoryAvailableAsync(projectId, afterServerSeq, ct);
        var batch = await db.Updates.AsNoTracking()
            .Where(update => update.ProjectId == projectId && update.ServerSeq > afterServerSeq)
            .OrderBy(update => update.ServerSeq).Take(128)
            .Select(update => new SequencedEnvelope(update.ServerSeq,
                new Envelope(update.Header, update.Nonce, update.Ciphertext, update.Signature)))
            .ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return batch;
    }
}
