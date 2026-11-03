using System.Text.Json;

namespace Novalist.Sync.Server.Relay;

internal enum RelayTokenState { Active, Inactive, Unavailable }

internal static class RelayIntrospectionResponse
{
    public static RelayTokenState Read(ReadOnlyMemory<byte> bytes, RelayIdentity identity, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("active", out var active)) return RelayTokenState.Unavailable;
        if (active.ValueKind == JsonValueKind.False) return RelayTokenState.Inactive;
        if (active.ValueKind != JsonValueKind.True) return RelayTokenState.Unavailable;
        if (value.TryGetProperty("iss", out var issuer) && issuer.ValueKind != JsonValueKind.String ||
            value.TryGetProperty("sub", out var subject) && subject.ValueKind != JsonValueKind.String)
            return RelayTokenState.Unavailable;
        if (!Matches(value, "iss", identity.Issuer) || !Matches(value, "sub", identity.Subject)) return RelayTokenState.Inactive;
        if (value.TryGetProperty("exp", out var expiry))
        {
            if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetDouble(out var seconds) || !double.IsFinite(seconds))
                return RelayTokenState.Unavailable;
            if (seconds <= (now - DateTimeOffset.UnixEpoch).TotalSeconds) return RelayTokenState.Inactive;
        }
        return RelayTokenState.Active;
    }

    private static bool Matches(JsonElement value, string claim, string expected) =>
        !value.TryGetProperty(claim, out var actual) || actual.ValueKind == JsonValueKind.String && actual.GetString() == expected;
}
