namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayTestClockTests
{
    [Fact]
    public void AdvancingAcrossSeveralPeriodsFiresEveryTickWithoutShiftingThePhase()
    {
        var clock = new RelayTestClock();
        var ticks = 0;
        using var timer = clock.CreateTimer(_ => ticks++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(3, ticks);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(4, ticks);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(7, ticks);
        timer.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(7, ticks);
    }
}
