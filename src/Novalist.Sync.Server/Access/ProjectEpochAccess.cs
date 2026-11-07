using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

public sealed partial class ProjectEpochAccess(SyncDbContext db, ProjectAccess projects, ILogger<ProjectEpochAccess>? logger = null)
{
    // A reservation stays alive while the owner keeps staging; it expires after this long without activity.
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(2);
    // Upper bound for one rotation, however active, so pushes are never blocked indefinitely.
    public static readonly TimeSpan ReservationMaxLifetime = TimeSpan.FromMinutes(30);
    private readonly ILogger<ProjectEpochAccess> log = logger ?? NullLogger<ProjectEpochAccess>.Instance;

    internal static Expression<Func<ProjectEpochStage, bool>> ExpiredAt(DateTimeOffset now)
    {
        var idleSince = now - ReservationLifetime;
        var startedBefore = now - ReservationMaxLifetime;
        return stage => stage.LastActivityAt <= idleSince || stage.CreatedAt <= startedBefore;
    }

    private static bool IsExpired(ProjectEpochStage stage, DateTimeOffset now) =>
        stage.LastActivityAt <= now - ReservationLifetime || stage.CreatedAt <= now - ReservationMaxLifetime;

    public async Task<ProjectEpochStatus> StatusAsync(Guid user, Guid project, CancellationToken ct)
    {
        await projects.EnsureCanReadAsync(user, project, ct);
        return await db.Projects.AsNoTracking().Where(value => value.Id == project)
            .Select(value => new ProjectEpochStatus((uint)value.CurrentEpoch, value.LastServerSeq)).SingleAsync(ct);
    }

    public async Task RegisterAsync(Guid user, Guid device, Guid project, uint epoch, byte[] certificate, CancellationToken ct)
    {
        ValidateCertificate(certificate);
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, false, ct);
        await EnsureDeviceAsync(user, device, ct);
        var current = await LockProjectAsync(project, ct);
        await projects.EnsureCanReadAsync(user, project, ct);
        if (current.CurrentEpoch != epoch) throw new ConflictException("Refresh the project encryption keys first.");
        var stored = await db.ProjectDeviceAuthorizations.FindAsync([project, device], ct);
        if (stored is not null)
        {
            if (stored.Epoch != epoch || !stored.Certificate.AsSpan().SequenceEqual(certificate))
                throw new ConflictException("The project device certificate changed.");
        }
        else
        {
            var owner = await db.Memberships.AnyAsync(value => value.ProjectId == project && value.UserId == user && value.Role == MembershipRole.Owner, ct);
            var hash = SHA256.HashData(certificate);
            if (!owner && !await db.Invitations.AnyAsync(value => value.ProjectId == project && value.RedeemedBy == user &&
                value.ActivatedDeviceId == device && value.CertificateHash == hash, ct))
                throw new AccessDeniedException("Activate this device's invitation before registering its project certificate.");
            db.ProjectDeviceAuthorizations.Add(new() { ProjectId = project, DeviceId = device, Epoch = epoch, Certificate = certificate.ToArray() });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectDevice>> DevicesAsync(Guid user, Guid project, CancellationToken ct)
    {
        await projects.EnsureIsOwnerAsync(user, project, ct);
        return await DeviceQuery(project).ToListAsync(ct);
    }

    private IQueryable<ProjectDevice> DeviceQuery(Guid project) =>
        from authorization in db.ProjectDeviceAuthorizations.AsNoTracking()
        join device in db.Devices on authorization.DeviceId equals device.Id
        join member in db.Memberships on new { authorization.ProjectId, device.UserId } equals new { member.ProjectId, member.UserId }
        where authorization.ProjectId == project && device.RevokedAt == null
        select new ProjectDevice(device.UserId, device.Id, (uint)authorization.Epoch, authorization.Certificate);

    public async Task BeginAsync(Guid user, Guid device, BeginProjectEpoch request, CancellationToken ct)
    {
        if (request.RotationId == Guid.Empty || request.PreviousEpoch == uint.MaxValue || request.PreviousServerSeq < 0)
            throw new FormatException("Invalid project epoch transition.");
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await EnsureDeviceAsync(user, device, ct);
        var project = await LockProjectAsync(request.ProjectId, ct);
        await projects.EnsureIsOwnerAsync(user, request.ProjectId, ct);
        CheckBoundary(project, request.PreviousEpoch, request.PreviousServerSeq);
        var now = await DatabaseTimeAsync(ct);
        await db.ProjectEpochStages.Where(value => value.ProjectId == request.ProjectId).Where(ExpiredAt(now)).ExecuteDeleteAsync(ct);
        await EnsureReservationAvailableAsync(request.ProjectId, request.RotationId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO project_epoch_stages (id, project_id, device_id, previous_epoch, previous_server_seq, created_at, last_activity_at, count)
            VALUES ({request.RotationId}, {request.ProjectId}, {device}, {(long)request.PreviousEpoch},
                {request.PreviousServerSeq}, {now}, {now}, 0)
            ON CONFLICT (id) DO NOTHING
            """, ct);
        var stage = await db.ProjectEpochStages.AsNoTracking().SingleAsync(value => value.Id == request.RotationId, ct);
        if (stage.ProjectId != request.ProjectId || stage.DeviceId != device ||
            stage.PreviousEpoch != request.PreviousEpoch || stage.PreviousServerSeq != request.PreviousServerSeq)
            throw new ConflictException("The rotation identifier is already in use.");
        await transaction.CommitAsync(ct);
    }

    public async Task<long> StageAsync(Guid user, Guid device, Guid rotation, Envelope envelope, CancellationToken ct)
    {
        var header = EnvelopeHeader.Parse(envelope.Header);
        if (header.MsgType != MsgType.Update || header.SenderDeviceId != device || header.SenderSeq > long.MaxValue ||
            envelope.Nonce is not { Length: 24 } || envelope.Signature is not { Length: 64 } || envelope.Ciphertext is not { Length: > 16 and <= 24592 })
            throw new FormatException("Invalid staged checkpoint envelope.");
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await EnsureDeviceAsync(user, device, ct);
        var stage = await LockStageForUploadAsync(rotation, device, ct);
        await projects.EnsureIsOwnerAsync(user, stage.ProjectId, ct);
        if (header.ProjectId != stage.ProjectId || header.Epoch != stage.PreviousEpoch + 1)
            throw new ConflictException("The checkpoint belongs to another transition.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var existing = await db.ProjectEpochEnvelopes.SingleOrDefaultAsync(value => value.RotationId == rotation && value.SenderSeq == (long)header.SenderSeq, ct);
        if (existing is not null)
        {
            if (!existing.Payload.AsSpan().SequenceEqual(payload)) throw new ConflictException("The staged sender sequence was reused.");
            await transaction.CommitAsync(ct);
            return existing.Index;
        }
        db.ProjectEpochEnvelopes.Add(new() { RotationId = rotation, Index = ++stage.Count, SenderSeq = (long)header.SenderSeq, Payload = payload });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return stage.Count;
    }

    // afterEpoch -1 returns the latest grant, including one for epoch 0 from AuthorizeDeviceAsync.
    public async Task<StoredProjectEpochGrant?> ReadGrantAsync(Guid user, Guid device, Guid project, long afterEpoch, CancellationToken ct)
    {
        await projects.EnsureCanReadAsync(user, project, ct);
        if (!await db.Devices.AnyAsync(value => value.Id == device && value.UserId == user && value.RevokedAt == null, ct))
            throw new AccessDeniedException("The receiving device is unavailable.");
        return await db.ProjectEpochKeyGrants.AsNoTracking().Where(value => value.ProjectId == project && value.DeviceId == device && value.Epoch > afterEpoch)
            .OrderByDescending(value => value.Epoch).Select(value => new StoredProjectEpochGrant((uint)value.Epoch, value.FirstServerSeq, value.GrantorDeviceId, value.Payload))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<Project> LockProjectAsync(Guid project, CancellationToken ct) =>
        (await db.Projects.FromSqlInterpolated($"SELECT * FROM projects WHERE id = {project} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
        ?? throw new AccessDeniedException("The shared project is unavailable.");

    private async Task<ProjectEpochStage> LockStageForUploadAsync(Guid rotation, Guid device, CancellationToken ct)
    {
        var projectId = await db.ProjectEpochStages.AsNoTracking().Where(stage => stage.Id == rotation)
            .Select(stage => (Guid?)stage.ProjectId).SingleOrDefaultAsync(ct)
            ?? throw new ConflictException("The project transition is unavailable.");
        // A second stage update can recheck the project FK; take its lock before the stage lock.
        await LockProjectAsync(projectId, ct);
        return await LockStageAsync(rotation, device, ct, projectId);
    }

    private async Task<ProjectEpochStage> LockStageAsync(Guid rotation, Guid device, CancellationToken ct, Guid? expectedProject = null)
    {
        var stage = (await db.ProjectEpochStages.FromSqlInterpolated($"SELECT * FROM project_epoch_stages WHERE id = {rotation} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        var now = await DatabaseTimeAsync(ct);
        if (stage is null || stage.DeviceId != device || (expectedProject.HasValue && stage.ProjectId != expectedProject) || IsExpired(stage, now))
            throw new ConflictException("The project transition is unavailable or expired.");
        stage.LastActivityAt = now;
        await db.SaveChangesAsync(ct);
        return stage;
    }

    private Task<DateTimeOffset> DatabaseTimeAsync(CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private async Task EnsureDeviceAsync(Guid user, Guid device, CancellationToken ct)
    {
        if (!await DeviceRegistry.ActiveDevices(db).AnyAsync(value => value.Id == device && value.UserId == user, ct))
            throw new AccessDeniedException("The initiating device is unavailable.");
    }

    private static void CheckBoundary(Project project, long epoch, long sequence)
    {
        if (project.CurrentEpoch != epoch || project.LastServerSeq != sequence)
            throw new ConflictException("The project changed during key rotation. Refresh and try again.");
    }

    private static void ValidateCertificate(byte[] certificate)
    {
        if (certificate is not { Length: >= 182 and <= 332 }) throw new FormatException("Invalid public project certificate size.");
    }

    private static void ValidateGrant(ProjectEpochGrant grant)
    {
        ValidateCertificate(grant.Certificate);
        if (grant.DeviceId == Guid.Empty || grant.Payload is not { Length: > 0 and <= 8192 })
            throw new FormatException("Invalid opaque epoch grant.");
    }
}
