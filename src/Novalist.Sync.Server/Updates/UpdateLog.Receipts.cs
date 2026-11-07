using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Updates;

public sealed partial class UpdateLog
{
    internal async Task<long?> FindDuplicateAsync(EnvelopeHeader header, long senderSeq, Envelope envelope, CancellationToken ct)
    {
        // One snapshot sees either the full update or its receipt when cleanup moves it.
        var existing = await db.Database.SqlQuery<DuplicateUpdate>($"""
            SELECT server_seq,
                (header = {envelope.Header} AND nonce = {envelope.Nonce}
                 AND ciphertext = {envelope.Ciphertext} AND signature = {envelope.Signature}) AS identical
            FROM updates
            WHERE project_id = {header.ProjectId} AND sender_device_id = {header.SenderDeviceId} AND sender_seq = {senderSeq}
            UNION ALL
            SELECT server_seq,
                (header_hash = sha256({envelope.Header}) AND nonce_hash = sha256({envelope.Nonce})
                 AND ciphertext_hash = sha256({envelope.Ciphertext}) AND signature_hash = sha256({envelope.Signature})) AS identical
            FROM update_receipts
            WHERE project_id = {header.ProjectId} AND sender_device_id = {header.SenderDeviceId} AND sender_seq = {senderSeq}
            """).SingleOrDefaultAsync(ct);
        if (existing is null) return null;
        return existing.Identical ? existing.ServerSeq : throw new PushRejectedException(
            $"senderSeq {senderSeq} of device {header.SenderDeviceId} was already used for a different update.");
    }

    private sealed record DuplicateUpdate(long ServerSeq, bool Identical);
}
