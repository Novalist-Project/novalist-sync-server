using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

// Owners authorize members' devices that never registered a project certificate (a second
// laptop, a recovery replacement) and hand them the current epoch key in the same transaction.
public sealed partial class ProjectEpochAccess
{
    public async Task<IReadOnlyList<PendingProjectDevice>> PendingDevicesAsync(Guid user, Guid project, CancellationToken ct)
    {
        await projects.EnsureIsOwnerAsync(user, project, ct);
        var members = db.Memberships.Where(value => value.ProjectId == project).Select(value => value.UserId);
        var devices = await db.Devices.AsNoTracking()
            .Where(value => members.Contains(value.UserId) && value.SigningPublicKey != null && value.ReceivingPublicKey != null)
            .ToDictionaryAsync(value => value.Id, ct);
        var authorized = (await db.ProjectDeviceAuthorizations.AsNoTracking().Where(value => value.ProjectId == project)
            .Select(value => value.DeviceId).ToListAsync(ct)).ToHashSet();
        return devices.Values.Where(value => value.RevokedAt == null && !authorized.Contains(value.Id))
            .OrderBy(value => value.UserId).ThenBy(value => value.Id)
            .Select(value => new PendingProjectDevice(value.UserId, LinkChain(value, devices)))
            .ToList();
    }

    private static List<ProjectDeviceLink> LinkChain(Device device, IReadOnlyDictionary<Guid, Device> devices)
    {
        var chain = new List<ProjectDeviceLink>();
        var current = device;
        while (current is not null && chain.Count < ProjectDeviceAuthorizationLimits.MaximumLinkChain)
        {
            chain.Add(new(new(current.Id, current.SigningPublicKey!, current.ReceivingPublicKey!),
                current.LinkedByDeviceId, current.LinkingSignature, current.RevokedAt != null));
            current = current.LinkedByDeviceId is { } parent && devices.TryGetValue(parent, out var linker) && linker.UserId == device.UserId
                ? linker
                : null;
        }
        return chain;
    }

    public async Task AuthorizeDeviceAsync(Guid user, Guid device, AuthorizeProjectDevice request, CancellationToken ct)
    {
        if (request?.Grant is null) throw new FormatException("A device key grant is required.");
        var grant = request.Grant;
        ValidateGrant(grant);
        await projects.EnsureIsOwnerAsync(user, request.ProjectId, ct);
        var member = await db.Devices.AsNoTracking().Where(value => value.Id == grant.DeviceId)
            .Select(value => (Guid?)value.UserId).SingleOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("The device is unavailable.");
        // Locking the device's account serializes with its revocation and with member removal.
        await using var transaction = await AccountAccessLock.AcquireManyAsync(db, [user, member], ct);
        await EnsureDeviceAsync(user, device, ct);
        var project = await LockProjectAsync(request.ProjectId, ct);
        await projects.EnsureIsOwnerAsync(user, project.Id, ct);
        if (project.CurrentEpoch != request.Epoch) throw new ConflictException("Refresh the project encryption keys first.");
        if (!await db.ProjectDeviceAuthorizations.AnyAsync(value => value.ProjectId == project.Id && value.DeviceId == device &&
            value.Epoch == project.CurrentEpoch, ct))
            throw new AccessDeniedException("Register this device's current project certificate before authorizing other devices.");
        await EnsureNoRotationAsync(project.Id, ct);
        await EnsureAuthorizableAsync(project.Id, member, grant.DeviceId, ct);
        if (await IsSameAuthorizationAsync(project, device, grant, ct))
        {
            await transaction.CommitAsync(ct);
            return;
        }
        db.ProjectDeviceAuthorizations.Add(new()
        {
            ProjectId = project.Id,
            DeviceId = grant.DeviceId,
            Epoch = project.CurrentEpoch,
            Certificate = grant.Certificate.ToArray()
        });
        // Everything from the history boundary on is in the current epoch, so this one key is enough to catch up.
        db.ProjectEpochKeyGrants.Add(new()
        {
            ProjectId = project.Id,
            DeviceId = grant.DeviceId,
            Epoch = project.CurrentEpoch,
            FirstServerSeq = project.HistoryStartsAt,
            GrantorDeviceId = device,
            Payload = grant.Payload.ToArray()
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        log.LogInformation("Device {GrantorDeviceId} authorized device {DeviceId} for project {ProjectId} at epoch {Epoch}.",
            device, grant.DeviceId, project.Id, project.CurrentEpoch);
    }

    private async Task EnsureNoRotationAsync(Guid project, CancellationToken ct)
    {
        var now = await DatabaseTimeAsync(ct);
        await db.ProjectEpochStages.Where(value => value.ProjectId == project).Where(ExpiredAt(now)).ExecuteDeleteAsync(ct);
        // A rotation commit needs grants for exactly the registered devices, so a device added mid-rotation would break it.
        if (await db.ProjectEpochStages.AnyAsync(value => value.ProjectId == project, ct))
            throw new ConflictException("A project key rotation is in progress. Authorize the device after it finishes.");
    }

    private async Task EnsureAuthorizableAsync(Guid project, Guid member, Guid target, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(value => value.ProjectId == project && value.UserId == member, ct))
            throw new AccessDeniedException("The device's account is not a member of this project.");
        if (!await db.Devices.AnyAsync(value => value.Id == target && value.UserId == member && value.RevokedAt == null &&
            value.SigningPublicKey != null && value.ReceivingPublicKey != null, ct))
            throw new AccessDeniedException("The device is unavailable.");
    }

    // An identical retry succeeds; any other existing authorization or grant is a conflict.
    private async Task<bool> IsSameAuthorizationAsync(Project project, Guid grantor, ProjectEpochGrant grant, CancellationToken ct)
    {
        var stored = await db.ProjectDeviceAuthorizations.FindAsync([project.Id, grant.DeviceId], ct);
        var key = await db.ProjectEpochKeyGrants.FindAsync([project.Id, grant.DeviceId, project.CurrentEpoch], ct);
        if (stored is null && key is null) return false;
        if (stored is not null && key is not null && stored.Epoch == project.CurrentEpoch && key.GrantorDeviceId == grantor &&
            stored.Certificate.AsSpan().SequenceEqual(grant.Certificate) && key.Payload.AsSpan().SequenceEqual(grant.Payload))
            return true;
        throw new ConflictException("The device is already authorized for this project.");
    }
}
