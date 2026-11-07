# Transport security and limits

These rules apply to the whole server, in persistence mode (the default) and in
relay mode. Relay endpoints add their own quotas on top; see [relay.md](relay.md).
All settings live in the `Transport` section (environment variables
`Transport__<Name>`).

## TLS and reverse proxies

The server rejects plain HTTP with 400 `Use HTTPS.` before authentication. HTTP is
accepted only in the Development environment when both socket addresses are
loopback addresses. Rejecting an HTTP request cannot undo credentials already sent
over it, so never expose an HTTP listener to clients.

Terminate TLS at Kestrel or at one trusted reverse proxy. With a proxy, set
`Transport__KnownProxies__0` (and further indexed entries) to its exact IP address.
Only these addresses may supply `X-Forwarded-For` and `X-Forwarded-Proto`; the proxy
must send both and overwrite client-supplied values. Forwarding is off unless
configured, and the server trusts at most one hop. The forwarded client address is
the one every per-address limit below uses. `Relay__KnownProxies__*` still works as
a deprecated alias when `Transport__KnownProxies__*` is empty. Invalid addresses
prevent startup.

## Access token lifetime

A hub connection never outlives its access token. When the token's `exp` passes,
the server closes the connection (`CloseOnAuthenticationExpiration`). Clients
obtain a fresh token with their refresh token, reconnect, rejoin their projects and
catch up from their last `serverSeq`. Device revocation and member removal still
end access immediately, independent of token expiry.

## Request limits

HTTP requests are limited per client address (one IPv4 address, or one IPv6 /64),
per account (token issuer and subject, after authentication) and for the whole
instance. A request must fit all three; otherwise the response is 429 without
`Retry-After`. Allowances are token buckets that refill every second, at one sixtieth
of the per-minute value (rounded up).

| Setting | Default | Meaning |
| --- | --- | --- |
| `RequestsPerMinute` | 4096 | Requests across the instance |
| `RequestsPerMinutePerIp` | 120 | Requests per client address |
| `RequestsPerMinutePerAccount` | 120 | Requests per authenticated account |

Counted: every HTTP endpoint, hub negotiation, and the request that opens a hub
connection. Not counted: requests that carry the connection token of an already
admitted hub connection (long-polling and server-sent-event traffic), which the
connection and call limits below cover, and `/relay/v1`, which has its own quotas.

## Hub connections and calls

| Setting | Default | Meaning |
| --- | --- | --- |
| `MaxConnections` | 4096 | Open hub connections on the instance |
| `MaxConnectionsPerIp` | 32 | Open hub connections per client address |
| `MaxConnectionsPerAccount` | 16 | Open hub connections per account |
| `HubCallBurst` | 200 | Hub calls one connection can make at once |
| `HubCallsPerSecond` | 20 | Sustained hub calls per connection |

A connection over a cap is closed right after the handshake with
`Too many open connections. Close another connection and retry.` Closing a
connection frees its slot immediately. Connection counts are kept in memory per
instance.

Each hub call, including the start of a streaming call such as `CatchUp` or
`ReportBlobReferences`, takes one token from its connection's bucket. A call
without a token fails with `Too many requests on this connection. Slow down and
retry.`; the connection stays open. Message sizes remain limited separately
(128 KiB per hub message).

Logs for refused connections contain only account and device IDs.
