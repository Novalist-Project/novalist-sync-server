using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Updates;

public sealed partial class UpdateLog(SyncDbContext db)
{
    // committingStage: the reservation whose own checkpoint is being copied in; it must not block itself.
    public async Task<long> AppendAsync(Guid userId, Envelope envelope, CancellationToken cancellationToken, Guid? committingStage = null)
    {
        var header = EnvelopeHeader.Parse(envelope.Header);

        if (header.MsgType != MsgType.Update)
        {
            throw new PushRejectedException($"Expected msgType {MsgType.Update}, got {header.MsgType}.");
        }

        if (header.SenderSeq > long.MaxValue)
        {
            throw new PushRejectedException($"SenderSeq {header.SenderSeq} is out of range.");
        }

        var senderSeq = (long)header.SenderSeq;
        long epoch = header.Epoch;
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var projects = await db.Projects.FromSqlInterpolated(
            $"SELECT * FROM projects WHERE id = {header.ProjectId} FOR UPDATE").AsNoTracking().ToListAsync(cancellationToken);
        var project = projects.SingleOrDefault();
        if (project is null || !await db.Memberships.AnyAsync(member => member.ProjectId == header.ProjectId &&
            member.UserId == userId && (member.Role == MembershipRole.Owner || member.Role == MembershipRole.Contributor), cancellationToken))
            throw new PushRejectedException($"No write access to project {header.ProjectId}.");
        var duplicate = await FindDuplicateAsync(header, senderSeq, envelope, cancellationToken);
        if (duplicate is { } previous) return previous;
        if (project.CurrentEpoch != epoch)
            throw new PushRejectedException("The update epoch is no longer current. Refresh the project encryption keys.");
        // Lock even apparently expired rows: staging may be renewing them in another transaction.
        var stages = await db.ProjectEpochStages.FromSqlInterpolated($"""
            SELECT * FROM project_epoch_stages WHERE project_id = {header.ProjectId} AND previous_epoch = {epoch}
            FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        if (stages.Any(stage => stage.Id != committingStage && stage.LastActivityAt > now - ProjectEpochAccess.ReservationLifetime &&
            stage.CreatedAt > now - ProjectEpochAccess.ReservationMaxLifetime))
            throw new PushRejectedException(Protocol.TransitionInProgress);

        var owner = (int)MembershipRole.Owner;
        var contributor = (int)MembershipRole.Contributor;

        // One statement: bumping the project's counter and inserting the update
        // either both happen or neither does, so serverSeq has no gaps. The UPDATE
        // locks the project row, which queues concurrent pushes to the same project.
        // Don't compose further LINQ onto this query (e.g. SingleAsync): EF would then
        // wrap the INSERT in a subquery, which PostgreSQL rejects.
        var assigned = await db.Database
            .SqlQuery<long>($"""
                                 WITH target AS MATERIALIZED (
                                    SELECT id FROM projects WHERE id = {header.ProjectId} FOR UPDATE
                                 ), writer AS (
                                    SELECT 1
                                    FROM memberships
                                    WHERE project_id IN (SELECT id FROM target)
                                        AND user_id = {userId}
                                        AND role IN ({owner}, {contributor})
                                    FOR SHARE
                                 ),
                                 seq AS (
                                    UPDATE projects
                                    SET last_server_seq = last_server_seq + 1
                                    WHERE id = {header.ProjectId} AND current_epoch = {epoch} AND EXISTS (SELECT 1 FROM writer)
                                    RETURNING last_server_seq
                                 )
                                 INSERT INTO updates (
                                    project_id, server_seq, item_id, epoch, sender_device_id, sender_seq,
                                    header, nonce, ciphertext, signature, received_at)
                                 SELECT
                                    {header.ProjectId}, seq.last_server_seq, {header.ItemId}, {epoch},
                                    {header.SenderDeviceId}, {senderSeq},
                                    {envelope.Header}, {envelope.Nonce}, {envelope.Ciphertext}, {envelope.Signature}, now()
                                 FROM seq
                                 RETURNING server_seq AS "Value"
                                 """).ToListAsync(cancellationToken);
        var sequence = assigned.Count == 1
            ? assigned[0]
            : throw new PushRejectedException($"No write access to project {header.ProjectId}.");
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return sequence;
    }
}
