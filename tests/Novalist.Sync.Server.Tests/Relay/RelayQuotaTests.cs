using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayQuotaTests
{
    [Fact]
    public void RejectedAttachmentDoesNotReserveAnAdditionalIdentity()
    {
        var clock = new QuotaClock();
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { RequestsPerMinute = 3, MaxRoutesPerIp = 1 }), clock);
        var owner = new RelayIdentity("192.0.2.1", "issuer", "owner");
        var attached = owner with { Address = "192.0.2.2" };
        using var route = Reserve(quotas, false, owner);
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.Equal(429, quotas.AdmitRouteAttachment(route, attached));
        Assert.Equal(2, quotas.EntryCount);
        using var unrelated = Reserve(quotas, false, attached with { Subject = "other" });
        unrelated.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, quotas.AdmitRouteAttachment(route, attached));
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.False(quotas.RequestUpgrade());
        route.Dispose();
        using var replacement = Reserve(quotas, false, attached);
    }

    [Fact]
    public void IdentityQuotaRejectionDoesNotConsumeAGlobalUpgradeToken()
    {
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { RequestsPerMinute = 3, MaxRoutesPerIp = 1 }), TimeProvider.System);
        var owner = new RelayIdentity("192.0.2.1", "issuer", "owner");
        using var route = Reserve(quotas, false, owner);
        using var occupied = Reserve(quotas, false, new RelayIdentity("192.0.2.2", "issuer", "other"));
        Assert.Equal(429, quotas.AdmitRouteAttachment(route, owner with { Address = "192.0.2.2" }));
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.False(quotas.RequestUpgrade());
    }

    [Fact]
    public void ConcurrentReservationsEnforceAccountAndIpLimitsAndReleaseOnce()
    {
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { MaxSessionsPerIp = 4, MaxSessionsPerAccount = 4 }), TimeProvider.System);
        var identity = new RelayIdentity("192.0.2.1", "issuer", "account");
        var accepted = new ConcurrentBag<RelayReservation>();
        Parallel.For(0, 50, _ =>
        {
            if (quotas.TryReserve(true, out var reservation, out var status, identity)) accepted.Add(reservation);
            else Assert.Equal(429, status);
        });
        Assert.Equal(4, accepted.Count);
        Assert.False(quotas.TryReserve(true, out _, out var accountLimit, identity with { Address = "192.0.2.2" }));
        Assert.Equal(429, accountLimit);
        Assert.False(quotas.TryReserve(true, out _, out var ipLimit, identity with { Subject = "another" }));
        Assert.Equal(429, ipLimit);
        foreach (var reservation in accepted) { reservation.Dispose(); reservation.Dispose(); }
        var refilled = Enumerable.Range(0, 4).Select(_ => Reserve(quotas, true, identity)).ToArray();
        try
        {
            Assert.False(quotas.TryReserve(true, out _, out var exhausted, identity));
            Assert.Equal(429, exhausted);
        }
        finally { foreach (var reservation in refilled) reservation.Dispose(); }
    }

    [Fact]
    public void QuotaPartitionsAreBoundedAndIdleEntriesExpireWithoutResettingLiveAccounts()
    {
        var clock = new QuotaClock();
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { MaxQuotaEntries = 3, MaxSessionsPerIp = 1, MaxSessionsPerAccount = 1 }), clock);
        var first = new RelayIdentity("192.0.2.1", "issuer", "account");
        using var active = Reserve(quotas, true, first);
        Assert.Equal(0, quotas.Request(first with { Address = "192.0.2.2" }, false));
        Assert.Equal(503, quotas.Request(first with { Address = "192.0.2.3" }, false));
        Assert.Equal(3, quotas.EntryCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, quotas.Request(first with { Address = "192.0.2.3" }, false));
        Assert.Equal(3, quotas.EntryCount);
        Assert.False(quotas.TryReserve(true, out _, out var sameAddress, first));
        Assert.Equal(429, sameAddress);
        Assert.False(quotas.TryReserve(true, out _, out var sameAccount, first with { Address = "192.0.2.3" }));
        Assert.Equal(429, sameAccount);
    }

    [Fact]
    public void RequestBudgetsApplyPerAccountAcrossIpsAndRefill()
    {
        var clock = new QuotaClock();
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { RequestsPerMinutePerAccount = 1 }), clock);
        var identity = new RelayIdentity("192.0.2.1", "issuer", "account");
        Assert.Equal(0, quotas.Request(identity, true));
        Assert.Equal(429, quotas.Request(identity with { Address = "192.0.2.2" }, true));
        Assert.Equal(0, quotas.Request(identity with { Subject = "another" }, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, quotas.Request(identity, true));
    }

    [Fact]
    public void ClockRollbackCannotRefillTheSameIntervalTwice()
    {
        var clock = new QuotaClock();
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { RequestsPerMinutePerAccount = 1 }), clock);
        var identity = new RelayIdentity("192.0.2.1", "issuer", "account");
        Assert.Equal(0, quotas.Request(identity, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, quotas.Request(identity, true));
        clock.Advance(TimeSpan.FromSeconds(-30));
        Assert.Equal(429, quotas.Request(identity, true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(429, quotas.Request(identity, true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(429, quotas.Request(identity, true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, quotas.Request(identity, true));
    }

    [Fact]
    public async Task BandwidthIsSharedAcrossRoutesAndCancellationReleasesBlockedSends()
    {
        var clock = new RelayTestClock();
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { BytesPerSecond = 16384, BurstBytes = 16384 }), clock);
        var owner = new RelayIdentity("192.0.2.1", "issuer", "owner");
        using var firstRoute = Reserve(quotas, false, owner, new("192.0.2.2", "issuer", "guest"));
        using var secondRoute = Reserve(quotas, false, owner, new("192.0.2.3", "issuer", "other"));
        await firstRoute.ThrottleAsync(16384, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var blocked = secondRoute.ThrottleAsync(16384, cancel.Token);
        Assert.False(blocked.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        clock.Advance(TimeSpan.FromSeconds(1));
        await secondRoute.ThrottleAsync(16384, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AttachChargesANewIpWithoutCountingTheAccountOrRouteTwice()
    {
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { MaxRoutes = 1, MaxRoutesPerIp = 1, MaxRoutesPerAccount = 1 }), TimeProvider.System);
        var owner = new RelayIdentity("192.0.2.1", "issuer", "owner");
        using var route = Reserve(quotas, false, owner);
        Assert.Equal(0, quotas.AdmitRouteAttachment(route, owner));
        Assert.Equal(0, quotas.AdmitRouteAttachment(route, owner with { Address = "192.0.2.2" }));
        Assert.Equal(0, quotas.AdmitRouteAttachment(route, owner with { Address = "192.0.2.2" }));
        Assert.Equal(3, quotas.EntryCount);
        route.Dispose();
        Assert.Equal(410, quotas.AdmitRouteAttachment(route, owner));
        using var next = Reserve(quotas, false, owner with { Address = "192.0.2.2" });
    }

    private static RelayReservation Reserve(RelayQuotas quotas, bool session, params RelayIdentity[] identities)
    {
        Assert.True(quotas.TryReserve(session, out var reservation, out _, identities));
        return Assert.IsType<RelayReservation>(reservation);
    }

    private sealed class QuotaClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
