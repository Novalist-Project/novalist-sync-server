# Live packet relay

Relay mode is opt-in (`Relay:Enabled=false` by default). Set `Relay__Enabled=true`
to forward packets without registering a database,
store project files, checkpoints, encrypted update history or offline queues.
Existing persistence endpoints are not mapped in relay mode. With relay disabled,
the persistent service and its database configuration remain in use.

Authentication is required by default (`Relay:RequireAuthentication=true`), using
the existing OIDC Bearer settings and native client sign-in configuration.
Authenticated relay deployments also require a confidential server client for
token introspection, configured as described below. This credential stays on the
server; it is separate from the public native desktop client.
For a relay without accounts, explicitly set `Relay__RequireAuthentication=false`;
no identity provider is needed. This setting applies only to relay mode: legacy
persistence mode always requires authentication. `/client-configuration` advertises
`authenticationRequired`; anonymous configurations return null authority, client ID
and scope. Novalist detects this policy and either starts browser sign-in when
sharing or joining, or asks for a display name and connects directly. Display names
in anonymous sessions are chosen labels, not verified account identities.

No `ConnectionStrings:Default`, PostgreSQL instance or migrations are needed in
relay mode. Start with `dotnet run --project src/Novalist.Sync.Server`. Remote clients
require HTTPS. The server rejects insecure relay requests before authentication;
HTTP is accepted only in the Development environment when both socket addresses
are loopback addresses.

Terminate TLS at Kestrel or at a trusted reverse proxy. With a proxy, configure
`Relay__KnownProxies__0` (and further indexed entries) with its exact IP address.
Only these proxies may supply `X-Forwarded-For` and `X-Forwarded-Proto`; they must
send both headers and overwrite any client-supplied values. Forwarding is disabled
unless configured, and the server trusts at most one proxy hop. Keep the internal
HTTP listener reachable only by that proxy and expose HTTPS to clients: rejecting
an HTTP request cannot undo credentials already transmitted over that connection.
See [ASP.NET Core proxy configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

When authentication is required, all WebSocket endpoints require a valid access
token in the Authorization header with a future `exp` claim. Each upgraded
connection returns `X-Novalist-Relay-Connection`, an opaque renewal identifier.
Clients obtain new access tokens using their locally held refresh token and send
`POST /relay/v1/connections/{id}/token` with the new Bearer token. Successful JWT
validation, identity matching and introspection update that connection in place
and return 204. The host keeps its session ID and invitations; both route sockets
renew independently. Refresh tokens never reach the relay. Rejected renewals do
not replace the current credentials. A replacement must expire later than the
current credential; equal or earlier expirations return 409. A stale
introspection result cannot terminate a renewed connection.
In anonymous mode, the host generates a fresh
256-bit random capability, encoded as 64 hexadecimal characters, and sends it in
`X-Novalist-Relay-Host` on its control connection and each route attachment.
Uppercase, lowercase and mixed-case hex encode the same capability. The server
decodes the 32 bytes before hashing, retains only their SHA-256 hash in memory,
checks it in constant time, and discards it when the session ends.
The capability must not be logged or included
in invitation links or query strings. Guests need no relay credential; pinned
end-to-end TLS and the host's invitation and device checks still control access
to the project. HTTPS remains required in either mode.

| Endpoint | Purpose |
| --- | --- |
| `/relay/v1/host` | Opens the host control connection; the first text message is the new session GUID. Subsequent text messages identify pending guest routes. |
| `/relay/v1/sessions/{sessionId}/guest` | Opens a guest stream while the host is online. The HTTP upgrade waits for the host to attach. |
| `/relay/v1/sessions/{sessionId}/routes/{routeId}` | Attaches the host side of a pending guest stream. Requires the control connection's issuer and subject, or its host capability in anonymous mode. |

Control GUIDs use 32 hexadecimal characters. Guest streams carry only binary
WebSocket frames. Each direction forwards at most 16 KiB per read, awaiting the
destination write before reading again. A route forwards WebSocket close status
and waits up to five seconds for the peer's close response before aborting.
An unattached guest request expires after `RouteTimeoutSeconds` (15 seconds by default). Pending routes remain
counted after the host reads their identifiers, and active routes remain counted
until they close. There is no project-sized buffering. Closing the control
socket removes its session and closes all routes. A new host receives a new ID;
closed session IDs and prior packet data cannot be resumed from this server.
When the control socket is writable, shutdown sends a WebSocket close frame and
allows one second for acknowledgement. Authorization expiry or revocation uses
status 1008 (`PolicyViolation`); an unavailable introspection provider or server
shutdown uses 1001 (`EndpointUnavailable`),
and a normal host disconnect uses 1000 (`NormalClosure`). Broken or unresponsive
sockets are aborted after that bounded attempt.

## Resource limits

Limits apply in both authenticated and anonymous mode. The server reserves
capacity before upgrading a connection and releases it on timeout, cancellation,
upgrade failure or disconnect. A route counts once against each distinct account
and IP used by its host and guest; attaching from a different IP adds that IP's
quota without charging the account twice. Both transfer directions share those
byte budgets. Reads and writes wait for capacity with bounded packet buffers,
so splitting traffic between several routes does not multiply an account's or
IP's bandwidth allowance.

All settings below are under `Relay` (for example,
`Relay__MaxRoutesPerSession=16` in environment variables). Values must be positive;
invalid limits prevent startup. The global upgrade allowance must be at least three
for the host control connection and both sides of its first route. Byte limits
must allow at least one 16 KiB packet.

| Setting | Default | Applies to |
| --- | ---: | --- |
| `MaxSessions` | 256 | Host sessions across the instance |
| `MaxSessionsPerIp` | 8 | Host sessions per IP |
| `MaxSessionsPerAccount` | 4 | Host sessions per issuer/subject |
| `MaxRoutes` | 1024 | Pending and active routes across the instance |
| `MaxRoutesPerIp` | 64 | Routes per participating IP |
| `MaxRoutesPerAccount` | 32 | Routes per participating issuer/subject |
| `MaxRoutesPerSession` | 16 | Pending and active routes for one host |
| `MaxPendingRoutesPerSession` | 4 | Unattached routes for one host |
| `RequestsPerMinute` | 1024 | Accepted WebSocket upgrade attempts across the instance |
| `RequestsPerMinutePerIp` | 60 | Connection attempts per IP, including rejected authentication |
| `RequestsPerMinutePerAccount` | 60 | WebSocket connection attempts per authenticated issuer/subject |
| `BytesPerSecond` | 8388608 | Sustained bytes per second per IP and account |
| `BurstBytes` | 16777216 | Initial and maximum byte allowance per IP and account |
| `MaxQuotaEntries` | 4096 | Maximum tracked IP/account quota entries; at least 2 in authenticated mode |
| `RouteTimeoutSeconds` | 15 | Time allowed for the host to attach |

Only the IP request allowance is charged before authentication. Ordinary HTTP
requests, including token renewals, do not charge the account or global allowance. Both allowances are
charged just before an authorized upgrade, after endpoint-specific access and
capacity checks; rejected authentication, missing routes and incorrect host
capabilities cannot consume it. The same rule applies to valid anonymous
upgrades when anonymous mode is enabled.

Request allowances refill continuously over one minute. Quota entries without
live sessions or routes can be discarded after a minute of inactivity when the
table reaches capacity; arbitrary addresses or accounts cannot grow it without
bound. IP addresses come from the socket or the explicitly trusted proxy described
above. IPv4 quotas use one address (including IPv4-mapped addresses); IPv6 quotas
use the /64 prefix so rotating interface addresses cannot evade limits.
Anonymous callers share those address/prefix limits, including callers behind
the same NAT.

The HTTP response is 429 for request or per-account/IP capacity limits and 503
for instance/session capacity limits, including a full quota table. These
responses omit `Retry-After` because capacity release time is unknown; they do
not create an accepted route or consume a live permit. A missing session
or route returns 404. Creating an anonymous host with a missing or malformed
capability returns 400; an incorrect attachment identity/capability returns 403. An
unattached route that times out or loses its host returns 410. Requests without
required authentication return 401. Authenticated tokens missing `iss` or `sub`
return 403. Limits are enforced before new requests
reach the hosting desktop; that desktop also caps its simultaneous attachments.
If host attachment admission hits a quota, the guest remains pending and the host
can retry the same route until its original attachment deadline. Rejected
attachments do not reserve live permits for additional identities or spend a
global upgrade token. Capacity checks may create idle entries in the bounded
quota table; those entries remain eligible for the usual inactivity eviction.

The server sees authenticated issuer/subject when sign-in is required, the host
capability on anonymous host requests, random session/route identifiers,
connection addresses, byte counts and timings. It never receives the participant
display names outside encrypted peer traffic. It never parses the tunneled TCP
protocol. Clients perform pinned TLS, invitation redemption, membership checks,
project comparison and file transfer with the hosting desktop. These metadata
fields replace the persistent protocol's project metadata in relay mode.

For the desktop, `RelayPeerTunnel` exposes a loopback TCP listener to the existing
peer transport. On the hosting desktop it connects route sockets to the existing
peer host's loopback port. TLS remains end-to-end between the two desktops.

## Token status and provider configuration

JWT signature validation cannot detect that an account has been disabled or its
token revoked. Every authenticated connection therefore checks the token with
the provider's [RFC 7662 introspection endpoint](https://www.rfc-editor.org/rfc/rfc7662.html)
before upgrade and every 30 seconds while connected. The provider must report
disabled accounts and revoked tokens as inactive. An inactive token closes the
connection; an inactive host control connection ends its entire session.
The relay also rejects a response whose optional `iss` or `sub` disagrees with
the authenticated identity, or whose optional `exp` has passed.

Set these required environment variables for authenticated relay deployments:

```text
Relay__Introspection__Endpoint=https://identity.example/introspect
Relay__Introspection__ClientId=novalist-relay
Relay__Introspection__ClientSecret=<server credential supplied through deployment secrets>
```

The endpoint must use HTTPS. HTTP is accepted only for a loopback endpoint in
Development. Credentials, query strings and fragments are forbidden in the
endpoint URL. The server sends the token in a form POST authenticated with the
confidential client's HTTP Basic credentials. It does not follow redirects, use
cookies, or log the token, request/response body or authorization header.
Missing or unsafe configuration prevents authenticated relay startup. Anonymous
relay mode requires no introspection endpoint or credentials; persistent mode's
configuration is unchanged.

An initial timeout, network error, malformed or
oversized response returns 503 without upgrading. An inactive token returns 401.
A failed periodic check preserves an existing connection for up to
`GracePeriodSeconds` since its last successful verification, and never beyond
that grace period after token expiry. Expired tokens are not polled as though
they were revoked; the client can replace them during the grace period. An
explicit inactive response terminates the connection immediately after the
check. Provider-reported revocation is observed within 30 seconds plus the
five-second request deadline by default. Grace exhaustion is checked on the
next monitor tick. New connections and renewals never receive an outage grace.
The check deadline includes concurrency-permit waits, response headers and body.

| Setting under `Relay:Introspection` | Default | Bound |
| --- | ---: | --- |
| `CheckIntervalSeconds` | 30 | 5–30 seconds between checks |
| `TimeoutSeconds` | 5 | 1–5 seconds for a check |
| `GracePeriodSeconds` | 300 | 0–900 seconds; zero disables grace |
| `MaxConcurrentRequests` | 16 | Maximum simultaneous provider requests |
| `MaxResponseBytes` | 16384 | Maximum response-body bytes |

Each connection monitors its token until it closes. Connections and admission
attempts are bounded by the relay limits above; no indefinite token cache or
background monitor survives connection teardown.

The server-owned relay tests exercise real Kestrel WebSocket connections,
ownership, status codes, host teardown, expiry and quota release. Separate quota
tests cover concurrent admission, bounded partition storage and shared bandwidth.
Introspection regressions cover disabled hosts and guests, provider outages,
identity mismatch, startup validation, bounded concurrency and response size,
redirect rejection, and captured-log credential checks.
