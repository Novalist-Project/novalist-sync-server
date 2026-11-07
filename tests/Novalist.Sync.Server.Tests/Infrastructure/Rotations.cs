using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// A complete owner rotation with a single-envelope checkpoint and one device, as the client does it.
internal static class Rotations
{
    public static async Task AuthorizeAsync(SyncDbContext db, Guid project, Guid device, CancellationToken ct)
    {
        db.ProjectDeviceAuthorizations.Add(new() { ProjectId = project, DeviceId = device, Epoch = 0, Certificate = new byte[182] });
        await db.SaveChangesAsync(ct);
    }

    public static async Task RotateAsync(ProjectEpochAccess epochs, Guid owner, Guid device, Guid project, ulong checkpointSenderSeq,
        CancellationToken ct)
    {
        var rotation = Guid.NewGuid();
        var reserved = await epochs.ReserveAsync(owner, device, project, rotation, ct);
        await epochs.StageAsync(owner, device, rotation, Update(project, device, checkpointSenderSeq, reserved.Epoch + 1), ct);
        await epochs.CommitAsync(owner, device, new(rotation, null, [new(device, new byte[182], new byte[128])]), ct);
    }

    public static Envelope Update(Guid project, Guid device, ulong senderSeq, uint epoch)
    {
        var envelope = TestData.Envelope(project, device, senderSeq);
        return envelope with { Header = (EnvelopeHeader.Parse(envelope.Header) with { Epoch = epoch }).ToBytes() };
    }
}
