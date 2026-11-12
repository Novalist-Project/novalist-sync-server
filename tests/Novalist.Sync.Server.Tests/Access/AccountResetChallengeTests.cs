using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class AccountResetChallengeTests
{
    [Fact]
    public void ChallengesExpireAreBoundedAndAllowRetryOnlyUntilSuccessfulCompletion()
    {
        var clock = new Clock();
        var challenges = new DeviceEnrollmentChallenges(clock);
        var user = Guid.NewGuid();
        var challenge = challenges.CreateReset(user);
        Assert.Throws<AccessDeniedException>(() => challenges.ClaimReset(Guid.NewGuid(), challenge.Id));
        using (var claim = challenges.ClaimReset(user, challenge.Id))
        {
            Assert.Throws<AccessDeniedException>(() => challenges.ClaimReset(user, challenge.Id));
            claim.Validate();
        }
        using (var retry = challenges.ClaimReset(user, challenge.Id)) retry.Complete();
        Assert.Throws<AccessDeniedException>(() => challenges.ClaimReset(user, challenge.Id));
        for (var index = 0; index < 8; index++) challenges.CreateReset(user);
        Assert.Throws<ConflictException>(() => challenges.CreateReset(user));
        clock.Now = clock.Now.AddMinutes(6);
        var expired = challenges.CreateReset(user);
        using var expiring = challenges.ClaimReset(user, expired.Id);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Throws<AccessDeniedException>(expiring.Validate);
        Assert.Throws<AccessDeniedException>(() => challenges.ClaimReset(user, expired.Id));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
