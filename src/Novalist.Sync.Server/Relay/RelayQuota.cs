namespace Novalist.Sync.Server.Relay;

internal sealed class RelayQuota(DateTimeOffset now, double requestLimit, double burstBytes)
{
    public int Sessions { get; set; }
    public int Routes { get; set; }
    public DateTimeOffset LastUsed { get; set; } = now;
    public RelayBucket Requests { get; } = new(now, requestLimit, requestLimit / 60);
    public RelayBucket Bytes { get; } = new(now, burstBytes, 0);
}

internal sealed class RelayBucket(DateTimeOffset now, double capacity, double rate)
{
    private readonly double maximum = capacity;
    private DateTimeOffset updated = now;
    private double available = capacity;

    public double Available(DateTimeOffset current, double? refillRate = null)
    {
        if (current <= updated) return available;
        available = Math.Min(maximum, available + (current - updated).TotalSeconds * (refillRate ?? rate));
        updated = current;
        return available;
    }

    public void Consume(int count) => available -= count;
}
