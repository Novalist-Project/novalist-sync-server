using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Hubs;

[Authorize]
public sealed partial class SyncHub(
    UserDirectory users,
    DeviceRegistry devices,
    DeviceProofServer proofs,
    ProjectAccess projects,
    ProjectEpochAccess epochs,
    AccessRevocation revocation,
    InvitationAccess invitations,
    UpdateLog updateLog,
    PresenceAccess presence,
    FieldLeaseAccess fieldLeases,
    ConnectionRegistry connections,
    ILogger<SyncHub> logger) : Hub<ISyncClient>, ISyncHub
{
    private const string UserIdItem = "userId";
    private const string DeviceIdItem = "deviceId";
    private const string ChallengeItem = "deviceChallenge";
    private const string AuthenticatedItem = "deviceAuthenticated";

    private Guid UserId => Context.Items.TryGetValue(UserIdItem, out var value) && value is Guid id ? id : throw new AccessDeniedException("The connection has no authenticated account.");
    private Guid DeviceId => Context.Items.TryGetValue(DeviceIdItem, out var value) && value is Guid id ? id : throw new AccessDeniedException("The connection has no registered device.");

    public override async Task OnConnectedAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        var deviceId = ReadDeviceId();
        var user = await users.GetOrCreateAsync(Context.User ?? throw new AccessDeniedException("Sign in before connecting."), cancellationToken);

        // Register before checking, so a revocation that lands in between still finds
        // this connection. If the check fails, the connection never counts as open.
        connections.Add(Context, user.Id, deviceId);

        try
        {
            await devices.EnsureActiveAsync(user.Id, deviceId, cancellationToken);
        }
        catch
        {
            connections.Remove(Context.ConnectionId);
            throw;
        }

        Context.Items[UserIdItem] = user.Id;
        Context.Items[DeviceIdItem] = deviceId;

        logger.LogInformation("Device {DeviceId} of user {UserId} connected.", deviceId, user.Id);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task<DeviceConnectionChallenge> GetDeviceChallenge()
    {
        if (Context.Items.TryGetValue(ChallengeItem, out var value) && value is DeviceConnectionChallenge existing) return Task.FromResult(existing);
        var serverUrl = proofs.ServerUrl ?? throw new HubException("Verified devices have not been configured.");
        var challenge = new DeviceConnectionChallenge(UserId, DeviceId, Guid.NewGuid(), serverUrl);
        Context.Items[ChallengeItem] = challenge;
        return Task.FromResult(challenge);
    }

    public async Task AuthenticateDevice(byte[] signature)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        if (!Context.Items.Remove(ChallengeItem, out var value) || value is not DeviceConnectionChallenge challenge)
            throw new AccessDeniedException("Request a fresh device challenge first.");
        await devices.VerifyConnectionAsync(challenge, signature, Context.ConnectionAborted);
        Context.Items[AuthenticatedItem] = true;
    }

    public async Task CreateProject(Guid projectId)
    {
        var cancellationToken = Context.ConnectionAborted;
        await using var access = await devices.PauseAccessChangesAsync(UserId, cancellationToken);
        await EnsureDeviceActiveAsync(cancellationToken);

        await projects.CreateAsync(UserId, projectId, cancellationToken);

        logger.LogInformation("User {UserId} created project {ProjectId}.", UserId, projectId);
    }

    public async Task JoinProject(Guid projectId)
    {
        var cancellationToken = Context.ConnectionAborted;
        await using var access = await devices.PauseAccessChangesAsync(UserId, cancellationToken);
        await using var delivery = await projects.PauseDeliveryAsync(projectId, cancellationToken);
        await EnsureDeviceActiveAsync(cancellationToken);
        await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);

        await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(projectId), cancellationToken);

        // A removal that ran between the check and joining the group could not see
        // this connection in the group yet; check again so it cannot slip through
        try
        {
            await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);
            connections.JoinProject(Context.ConnectionId, projectId);
        }
        catch
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ProjectGroup(projectId), cancellationToken);
            throw;
        }
    }

    public async Task LeaveProject(Guid projectId)
    {
        await using var delivery = await projects.PauseDeliveryAsync(projectId, Context.ConnectionAborted);
        connections.LeaveProject(Context.ConnectionId, projectId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ProjectGroup(projectId), Context.ConnectionAborted);
    }

    public async Task<long> Push(Envelope envelope)
    {
        var cancellationToken = Context.ConnectionAborted;
        await using var access = await devices.PauseAccessChangesAsync(UserId, cancellationToken);
        var header = EnvelopeHeader.Parse(envelope.Header);
        await using var delivery = await projects.PauseDeliveryAsync(header.ProjectId, cancellationToken);

        if (header.SenderDeviceId != DeviceId)
        {
            throw new PushRejectedException($"Sender device {header.SenderDeviceId} is not this device {DeviceId} of the connection.");
        }

        await EnsureDeviceActiveAsync(cancellationToken);
        await projects.EnsureCanWriteAsync(UserId, header.ProjectId, cancellationToken);

        var serverSeq = await updateLog.AppendAsync(UserId, envelope, cancellationToken);

        await Clients.OthersInGroup(ProjectGroup(header.ProjectId))
            .ReceiveUpdate(new SequencedEnvelope(serverSeq, envelope));

        logger.LogInformation(
            "Device {DeviceId} of user {UserId} pushed update {ServerSeq} to project {ProjectId} ({Size} bytes).",
            DeviceId, UserId, serverSeq, header.ProjectId, envelope.Ciphertext.Length);

        return serverSeq;
    }

    public async Task PublishPresence(Envelope envelope)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        var project = (await presence.PreflightAsync(UserId, DeviceId, envelope, Context.ConnectionAborted)).ProjectId;
        var targets = connections.PresenceTargets(project, Context.ConnectionId, PresenceAccess.MaximumRecipients + 1);
        if (targets.Count > PresenceAccess.MaximumRecipients)
            throw new PushRejectedException("Presence is limited to 256 active recipients per message.");
        await presence.DeliverAsync(UserId, DeviceId, envelope, targets.Select(target => target.DeviceId).ToHashSet(), (projectId, activeDevices, cancellationToken) =>
            ((Hub)this).Clients.Clients(connections.PresenceRecipients(projectId, activeDevices, targets))
                .SendCoreAsync(nameof(ISyncClient.ReceivePresence), [envelope], cancellationToken),
            Context.ConnectionAborted);
    }

    public async IAsyncEnumerable<SequencedEnvelope> CatchUp(Guid projectId, long afterServerSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await EnsureDeviceActiveAsync(cancellationToken);
            await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);
        }
        catch (Exception ex) when (ClientErrorFilter.IsClientError(ex))
        {
            // Hub filters don't see exceptions thrown while a stream is enumerated
            throw new HubException(ex.Message);
        }

        await foreach (var update in updateLog.ReadAfterAsync(projectId, afterServerSeq, cancellationToken))
        {
            await using var access = await devices.PauseAccessChangesAsync(UserId, cancellationToken);
            await using var delivery = await projects.PauseDeliveryAsync(projectId, cancellationToken);
            try
            {
                await EnsureDeviceActiveAsync(cancellationToken);
                await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);
            }
            catch (Exception ex) when (ClientErrorFilter.IsClientError(ex)) { throw new HubException(ex.Message); }
            yield return update;
        }
    }

    private Guid ReadDeviceId()
    {
        var value = Context.GetHttpContext()?.Request.Query[Protocol.DeviceIdParameter].ToString();

        return Guid.TryParse(value, out var deviceId)
            ? deviceId
            : throw new HubException($"Connect with ?{Protocol.DeviceIdParameter}=<guid>.");
    }

    private async Task<bool> EnsureDeviceActiveAsync(CancellationToken cancellationToken)
    {
        var key = await devices.AuthenticationKeyAsync(UserId, DeviceId, cancellationToken);
        if (key is not null && (!Context.Items.TryGetValue(AuthenticatedItem, out var authenticated) || !Equals(authenticated, true)))
            throw new AccessDeniedException("Prove possession of this device key before using the connection.");
        return key is not null;
    }

    internal static string ProjectGroup(Guid projectId) => $"project:{projectId}";
}
