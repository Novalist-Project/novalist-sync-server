using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;

namespace Novalist.Sync.Server.Access;

public sealed class AccountIdentityReset(SyncDbContext db, UserDirectory users, DeviceEnrollmentChallenges challenges,
    DeviceEnrollmentTokens tokens, AccessDeliveryGate delivery, ConnectionRegistry connections, TimeProvider clock,
    ILogger<AccountIdentityReset> log)
{
    public async Task<AccountIdentityResetResult> ResetAsync(ClaimsPrincipal principal, AccountIdentityResetRequest request, CancellationToken ct)
    {
        if (!request.ForfeitProjectAccess)
            throw new FormatException("Confirm that resetting this identity forfeits all existing project access.");
        var user = await users.GetOrCreateAsync(principal, ct);
        using var claim = challenges.ClaimReset(user.Id, request.ChallengeId);
        await tokens.ValidateAsync(principal, claim.Challenge, request.IdentityToken, ct);
        await using var paused = await delivery.AccountAsync(user.Id, ct);
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user.Id, legacyAccount: false, ct);
        await db.Entry(user).ReloadAsync(ct);
        // A concurrent reset must not archive its replacement or restore the old login mapping.
        if (user.Issuer != principal.FindFirstValue("iss") || user.Subject != principal.FindFirstValue("sub"))
            throw new ConflictException("This identity was already reset. Sign in again.");
        claim.Validate();
        var replacement = new User
        {
            Id = Guid.CreateVersion7(),
            Issuer = user.Issuer,
            Subject = user.Subject,
            Email = user.Email,
            UserName = user.UserName,
            CreatedAt = clock.GetUtcNow()
        };
        user.Issuer = "urn:novalist:archived-account:" + user.Id;
        user.Email = null;
        await db.Devices.Where(device => device.UserId == user.Id && device.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(device => device.RevokedAt, clock.GetUtcNow()), ct);
        // Free the unique issuer/subject mapping before inserting its replacement, within the same transaction.
        await db.SaveChangesAsync(ct);
        db.Users.Add(replacement);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        claim.Complete();
        connections.AbortUser(user.Id);
        log.LogInformation("Account identity {ArchivedUserId} archived and replaced by {UserId} after fresh MFA.", user.Id, replacement.Id);
        return new(replacement.Id);
    }
}
