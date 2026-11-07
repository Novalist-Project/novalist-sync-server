# Collaboration migrations

Each stacked change adds only the schema it uses: verified device keys,
invitations, epoch rotation, then field leases. Relay, membership management and
presence need no new tables. Migrations extend the original `InitialCreate`
schema; regression tests preserve populated users, devices, memberships and
updates through upgrade, rollback and reapplication.

Back up persistent databases before upgrading. Development databases that ran
the earlier combined `CollaborationAccess` migration or intermediate unreleased
migrations need recreation if disposable, or a separate data-preserving
reconciliation. Do not apply the stack over that unrelated migration history.
Relay deployments need no database or migrations.

`VerifiedDevices` adds unique signing/receiving public-key indexes and a foreign
key from each linked device to its authorizing device. Existing legacy records
have null keys and are preserved. Rolling it back discards verified key and link
metadata; restore a backup before resuming verified-device access after rollback.

Rolling back `Invitations` drops invitations and device activation receipts.
Back up those records before rollback; reapplying the migration creates empty
tables and cannot recover invitation links or activation retries.

`EpochCompaction` adds `projects.history_starts_at` (1 for projects that never
rotated), the `project_history_boundaries` table and a sliding
`project_epoch_stages.last_activity_at`. Existing reservations retain their start
time as their last activity; inserts that omit the new column default to the
database transaction time. Projects that already rotated are
backfilled from their stored key grants; their boundaries get the upgrade time,
so the compaction grace period starts with the upgrade. Rolling it back is
harmless until the cleanup job has deleted superseded updates. After that, a
rolled-back server would serve catch-up from before the boundary with gaps, so
restore a backup instead of rolling back.

`CompactedUpdateReceipts` adds compact retry receipts. Cleanup moves superseded
updates into receipts atomically, retaining sequence numbers and SHA-256 hashes
of each envelope field while deleting the payload bytes. No extension is needed;
PostgreSQL provides `sha256(bytea)`. Apply all compaction migrations before
starting the new server; stop the old server during upgrade. Existing updates
remain unchanged until cleanup runs. Receipts are retained until project deletion.
Rolling this migration back drops retry receipts and cannot restore pruned
payloads. After compaction, restore a backup before returning to an older server;
reapplying the migration alone cannot recover lost retry acknowledgements.

`HistoryBoundaryCreatedAtIndex` indexes boundary creation times for grace-period
cleanup and repairs the activity default on databases that applied an earlier
revision of `EpochCompaction`. Rolling it back removes only the index; the safe
`CURRENT_TIMESTAMP` default remains.

`BlobCollection` adds the `blobs` table: one row per stored blob with its byte
count and garbage-collection marks. It is backfilled from `blob_chunks`, with no
marks set, so no existing blob becomes collectable before two reference reports
a grace period apart. A composite foreign key cascades blob deletion to the
blob's chunks. Stop the old server before applying it: the migration locks
`blob_chunks` so concurrent uploads cannot break the backfill, but after it
commits an older server's uploads fail the new foreign key. Rolling it back
drops the table and its marks while keeping every chunk and the project byte
counters. After the cleanup job has collected blobs, rollback cannot restore
their chunks.
