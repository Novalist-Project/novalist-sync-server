namespace Novalist.Sync.Contracts;

public static class Protocol
{
    public const byte Version = 1;
    public const string HubPath = "/sync";
    public const string DevicesPath = "/devices";
    public const string DeviceIdParameter = "deviceId";
    public const string ClientConfigurationPath = "/client-configuration";
}

public enum MsgType : byte
{
    Update = 1,
    Snapshot = 2,
    Presence = 3
}
