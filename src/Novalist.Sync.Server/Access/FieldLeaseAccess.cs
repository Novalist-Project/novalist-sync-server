using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Access;

public sealed class FieldLeaseAccess(SyncDbContext db, DeviceRegistry devices, UpdateLog updates)
{
    public const int MaximumRetainedAttempts = 256;

    public Task<DateTimeOffset> TimeAsync(CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    public Task<FieldLeaseGrant> AcquireAsync(Guid userId, Guid deviceId, FieldLeaseRequest request, CancellationToken ct) =>
        WithinAsync(userId, deviceId, request, async (project, now) =>
        {
            if (AcquisitionDeadline(request, now) <= now)
                throw new ConflictException("This focus request expired. Start a new focus request.");
            var previous = await ReadAsync(request, ct);
            if (Owns(previous, deviceId, request, now))
                return Grant(previous, project, now);
            if (await db.FieldLeaseAttempts.AnyAsync(attempt => attempt.ProjectId == request.ProjectId && attempt.DeviceId == deviceId && attempt.LeaseId == request.LeaseId, ct))
                throw new ConflictException("A finished focus request needs a new lease identifier.");
            await RetireAsync(deviceId, request, now, ct);
            if (previous is not null && previous.Epoch == request.Epoch && previous.ExpiresAt > now)
                return Grant(previous, project, now);
            var expires = now.AddSeconds(30);
            long epoch = request.Epoch;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO field_leases (project_id, resource_id, device_id, lease_id, epoch, expires_at)
                VALUES ({request.ProjectId}, {request.ResourceId}, {deviceId}, {request.LeaseId}, {epoch}, {expires})
                ON CONFLICT (project_id, resource_id) DO UPDATE
                SET device_id = EXCLUDED.device_id, lease_id = EXCLUDED.lease_id,
                    epoch = EXCLUDED.epoch, expires_at = EXCLUDED.expires_at
                """, ct);
            return new FieldLeaseGrant(deviceId, request.LeaseId, expires, now, project.LastServerSeq);
        }, ct);

    public Task<FieldLeaseGrant?> RenewAsync(Guid userId, Guid deviceId, FieldLeaseRequest request, CancellationToken ct) =>
        WithinAsync<FieldLeaseGrant?>(userId, deviceId, request, async (project, now) =>
        {
            var lease = await ReadAsync(request, ct);
            if (!Owns(lease, deviceId, request, now)) return null;
            var expires = now.AddSeconds(30);
            await db.FieldLeases.Where(value => value.ProjectId == request.ProjectId && value.ResourceId == request.ResourceId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.ExpiresAt, expires), ct);
            return new FieldLeaseGrant(deviceId, request.LeaseId, expires, now, project.LastServerSeq);
        }, ct);

    public Task<bool> ReleaseAsync(Guid userId, Guid deviceId, FieldLeaseRequest request, CancellationToken ct) =>
        WithinAsync(userId, deviceId, request, async (_, now) =>
        {
            if (!Owns(await ReadAsync(request, ct), deviceId, request, now))
            {
                if (request.AcquireBefore is not null) await RetireAsync(deviceId, request, now, ct);
                return false;
            }
            await db.FieldLeases.Where(value => value.ProjectId == request.ProjectId && value.ResourceId == request.ResourceId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.ExpiresAt, DateTimeOffset.UnixEpoch), ct);
            return true;
        }, ct);

    public Task<long> PushAsync(Guid userId, Guid deviceId, FieldLeaseRequest request, Envelope envelope, CancellationToken ct)
        => PushCoreAsync(userId, deviceId, request, null, envelope, ct);

    public Task<long> PushGuardedAsync(Guid userId, Guid deviceId, FieldLeaseRequest request, FieldLeaseRequest required, Envelope envelope, CancellationToken ct)
    {
        if (required.ProjectId != request.ProjectId || required.Epoch != request.Epoch || required.ResourceId == Guid.Empty ||
            required.ResourceId == request.ResourceId || required.LeaseId == Guid.Empty)
            throw new PushRejectedException("The required editing lease does not belong to this change.");
        return PushCoreAsync(userId, deviceId, request, required, envelope, ct);
    }

    private Task<long> PushCoreAsync(Guid userId, Guid deviceId, FieldLeaseRequest request, FieldLeaseRequest? required, Envelope envelope, CancellationToken ct)
    {
        var header = EnvelopeHeader.Parse(envelope.Header);
        if (header.ProjectId != request.ProjectId || header.SenderDeviceId != deviceId || header.Epoch != request.Epoch ||
            header.MsgType != MsgType.Update || header.SenderSeq is 0 or > long.MaxValue)
            throw new PushRejectedException("The field edit does not belong to this lease context.");
        return WithinAsync(userId, deviceId, request, async (_, now) =>
        {
            var previous = await updates.FindDuplicateAsync(header, (long)header.SenderSeq, envelope, ct);
            if (previous is { } sequence) return sequence;
            if (!Owns(await ReadAsync(request, ct), deviceId, request, now))
                throw new ConflictException("This field's editing lease has expired or changed.");
            if (required is not null && !Owns(await ReadAsync(required, ct), deviceId, required, now))
                throw new ConflictException("The required editing lease has expired or changed.");
            return await updates.AppendAsync(userId, envelope, ct);
        }, ct);
    }

    private async Task<T> WithinAsync<T>(Guid userId, Guid deviceId, FieldLeaseRequest request,
        Func<Project, DateTimeOffset, Task<T>> action, CancellationToken ct)
    {
        if (request.ProjectId == Guid.Empty || request.ResourceId == Guid.Empty || request.LeaseId == Guid.Empty)
            throw new ArgumentException("Project, resource and lease identities are required.");
        await using var transaction = await AccountAccessLock.AcquireAsync(db, userId, legacyAccount: false, ct, shared: true);
        await devices.EnsureActiveAsync(userId, deviceId, ct);
        var projects = await db.Projects.FromSqlInterpolated($"SELECT * FROM projects WHERE id = {request.ProjectId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.Memberships.FromSqlInterpolated($"""
            SELECT * FROM memberships WHERE project_id = {request.ProjectId} AND user_id = {userId} FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        if (members.SingleOrDefault()?.Role is not (MembershipRole.Owner or MembershipRole.Contributor))
            throw new AccessDeniedException("Editing a shared field requires write access.");
        var project = projects.SingleOrDefault();
        if (project is null || project.CurrentEpoch != request.Epoch)
            throw new AccessDeniedException("This project's editing epoch is no longer current.");
        var now = await TimeAsync(ct);
        await db.FieldLeaseAttempts.Where(attempt => attempt.ProjectId == request.ProjectId &&
            attempt.DeviceId == deviceId && attempt.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        var result = await action(project, now);
        await transaction.CommitAsync(ct);
        return result;
    }

    private Task<FieldLease?> ReadAsync(FieldLeaseRequest request, CancellationToken ct) => db.FieldLeases.AsNoTracking()
        .SingleOrDefaultAsync(lease => lease.ProjectId == request.ProjectId && lease.ResourceId == request.ResourceId, ct);

    private async Task RetireAsync(Guid deviceId, FieldLeaseRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var expires = AcquisitionDeadline(request, now);
        if (expires <= now || await db.FieldLeaseAttempts.AnyAsync(attempt => attempt.ProjectId == request.ProjectId &&
            attempt.DeviceId == deviceId && attempt.LeaseId == request.LeaseId, ct)) return;
        if (await db.FieldLeaseAttempts.CountAsync(attempt => attempt.ProjectId == request.ProjectId && attempt.DeviceId == deviceId, ct) >= MaximumRetainedAttempts)
            throw new ConflictException("Too many recent focus requests. Wait for expired requests to clear.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO field_lease_attempts (project_id, device_id, lease_id, expires_at)
            VALUES ({request.ProjectId}, {deviceId}, {request.LeaseId}, {expires}) ON CONFLICT DO NOTHING
            """, ct);
    }

    private static DateTimeOffset AcquisitionDeadline(FieldLeaseRequest request, DateTimeOffset now)
    {
        if (request.AcquireBefore is not { } deadline || deadline > now.AddMinutes(2))
            throw new FormatException("A focus request needs an acquisition deadline no more than two minutes ahead.");
        return deadline;
    }

    private static bool Owns([NotNullWhen(true)] FieldLease? lease, Guid deviceId, FieldLeaseRequest request, DateTimeOffset now) =>
        lease is not null && lease.DeviceId == deviceId && lease.LeaseId == request.LeaseId && lease.Epoch == request.Epoch && lease.ExpiresAt > now;

    private static FieldLeaseGrant Grant(FieldLease lease, Project project, DateTimeOffset now) =>
        new(lease.DeviceId, lease.LeaseId, lease.ExpiresAt, now, project.LastServerSeq);
}
