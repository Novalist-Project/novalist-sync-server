namespace Novalist.Sync.Contracts;

public static class Protocol
{
    public const byte Version = 1;
    public const string HubPath = "/sync";
    public const string DevicesPath = "/devices";
    public const string DeviceRegistrationPath = "/devices/registration";
    public const string VerifiedDevicesPath = "/devices/verified";
    public const string DeviceEnrollmentPath = "/devices/enrollment";
    public const string DeviceIdParameter = "deviceId";
    public const string ClientConfigurationPath = "/client-configuration";
    // Push refusals while a rotation or compaction holds the project. Clients match this text and retry.
    public const string TransitionInProgress = "A project key rotation or history compaction is in progress. Retry shortly.";
    // Catch-up refusals for positions before the history boundary. Clients match this text and load the newest checkpoint.
    public const string HistoryReplaced = "This project's earlier history was replaced by a checkpoint.";
}

public enum MsgType : byte
{
    Update = 1,
    Snapshot = 2,
    Presence = 3
}
