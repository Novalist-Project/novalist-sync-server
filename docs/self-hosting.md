# Self-hosting Novalist Sync

Run one server instance with a public HTTPS hostname. The current Novalist desktop
integration uses **relay mode**: the hosting desktop must stay online, and the
server needs neither PostgreSQL nor a data volume. Persistent mode is a separate
protocol and remains the server's default; explicitly enable relay mode below.

The server never receives project encryption keys or plaintext project content.
See [relay.md](relay.md) for relay behavior and [design.md](design.md) for the
persistent protocol.

## Images and versions

Release images use this name:

```text
ghcr.io/qf0xb/novalist-sync-server:MAJOR.MINOR.PATCH
```

Publishing a GitHub release from `vMAJOR.MINOR.PATCH` builds that exact tag for
Linux AMD64 and ARM64. Prerelease tags may use `-alpha.N`, `-beta.N`, or `-rc.N`,
for example `v1.2.0-rc.1` produces image tag `1.2.0-rc.1`. The image's application
version and OCI version/revision labels come from the release tag and commit.
There are no `latest`, branch, major-only or minor-only image tags. A branch push
or an unpublished tag does not publish an image. Never move a published release
tag; release a new patch instead. Pin an image digest for immutable deployments.

The same release also publishes
`ghcr.io/qf0xb/novalist-sync-server-migrations:MAJOR.MINOR.PATCH` for persistent-mode
schema upgrades. Use matching server and migration versions. Both GHCR packages
need public visibility for unauthenticated pulls, or registry authentication.

The release workflow must finish before the image exists. On first publication,
the repository owner must make the GHCR package public for unauthenticated pulls,
or operators must authenticate with a token that can read the package.

Choose an existing release version, replacing the example below:

```bash
export VERSION=1.0.0
export IMAGE="ghcr.io/qf0xb/novalist-sync-server:$VERSION"
docker pull "$IMAGE"
```

## Complete production stack with Compose

Choose [the relay or persistent Compose example](../deploy/README.md). Both include
Caddy with automatic HTTPS, Keycloak with a preconfigured native client, PostgreSQL
for the identity provider, health checks and persistent volumes. Relay adds token
introspection. Persistent mode adds its own project database, a release-matched
migration job, and a password-plus-OTP enrollment flow.
Configure two public hostnames, a published Novalist image version and private
credentials in `deploy/.env`, then follow the deployment guide.

Use `deploy/compose.relay.yml` for the current desktop integration, or
`deploy/compose.persistent.yml` for persistent-protocol clients. Compose is for
production self-hosting; local development continues to use `devenv`.

## Quick start: relay without accounts, direct TLS

Requirements: Docker Engine, a DNS hostname pointing at this machine, an open
TCP port 443, and a trusted TLS certificate with its private key in PEM format.
Obtain and renew the certificate using your existing certificate tooling.

Create a private directory outside the source checkout, containing `fullchain.pem`
and `privkey.pem`. The image runs as UID 1654. Give that user read access to both
files and traversal access to the directory; keep the private key inaccessible
to other users. Use absolute paths in mount arguments.

Create `relay.env` outside the checkout, with mode `0600`:

```dotenv
Relay__Enabled=true
Relay__RequireAuthentication=false
ASPNETCORE_URLS=https://+:8443
Kestrel__Certificates__Default__Path=/tls/fullchain.pem
Kestrel__Certificates__Default__KeyPath=/tls/privkey.pem
AllowedHosts=sync.example.com
```

Replace `sync.example.com` with your hostname, then start the release image:

```bash
docker run -d --name novalist-sync --restart unless-stopped \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --cap-drop ALL --security-opt no-new-privileges=true \
  --env-file /absolute/path/relay.env \
  --mount type=bind,src=/absolute/path/tls,dst=/tls,readonly \
  -p 443:8443 "$IMAGE"
```

Set the desktop's server address to `https://sync.example.com/`. Anonymous mode
deliberately permits relay connections without an account. Project admission and
pinned end-to-end TLS remain the hosting desktop's responsibility. IP and
instance quotas still apply. The host's display name is not a verified identity.

Verify the public endpoint:

```bash
curl --fail --show-error https://sync.example.com/client-configuration
docker logs --tail 100 novalist-sync
```

Expect HTTP 200, protocol version 1, `authenticationRequired: false`, and null
authority/client ID/scope. Test sharing and joining from two desktops as well:
discovery alone does not test a WebSocket route. There is no dedicated health or
database readiness endpoint. Do not disable certificate validation for this check.

Restart the container after replacing certificate files. Relay sessions live in
memory; restarting ends all sessions and routes. Hosts must share fresh invitations
after reconnecting. No project data needs backing up on the relay.

## TLS at a reverse proxy

Instead of mounting certificates into the app, terminate TLS at an existing
reverse proxy. Remove `ASPNETCORE_URLS` and both `Kestrel__Certificates__*` settings
from the environment file. The app then listens on HTTP port 8080 internally.
Set `Transport__KnownProxies__0` to the proxy's exact source IP as seen by the app.
Keep that address stable, for example using a fixed IP on a private Docker network.
Do not expose the app's HTTP port publicly. Connect the proxy to the app over the
private network, or publish only to loopback for a proxy on the Docker host.

For a containerized proxy, replace the direct-TLS `docker run` command with the
following shared-network setup. Replace `nginx-proxy` with your existing proxy
container's name. Choose an unused subnet; if you change the proxy IP, change
`Transport__KnownProxies__0` to match. Preserve this network attachment and fixed
IP in the proxy's deployment configuration when recreating it. The app uses the
fixed address `172.30.50.3`; reserve and reuse it when recreating the app too.
If you change the subnet, keep both fixed addresses inside it and distinct.

If you already started the direct-TLS container, run `docker stop novalist-sync`
and `docker rm novalist-sync` first. This ends its active relay sessions and frees
port 443 for the proxy. The proxy owns the public port and TLS certificates;
the replacement app container has no published ports or certificate mounts.

```bash
docker network create --subnet 172.30.50.0/24 novalist-proxy
docker network connect --ip 172.30.50.2 novalist-proxy nginx-proxy

docker run -d --name novalist-sync --restart unless-stopped \
  --network novalist-proxy --ip 172.30.50.3 \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --cap-drop ALL --security-opt no-new-privileges=true \
  --env-file /absolute/path/relay.env \
  -e Transport__KnownProxies__0=172.30.50.2 \
  "$IMAGE"
```

Use the environment file with the direct-TLS settings removed as described above.
Docker resolves `novalist-sync` for the proxy because both containers share this
user-defined network. The proxy reaches the app on port 8080 without a `-p` mapping.
The app's fixed address also keeps nginx's startup DNS lookup valid across app
recreation; preserve that address or reload nginx after an address change.

The proxy must overwrite both forwarded headers and support long-lived WebSockets.
For an nginx proxy on the same private network, use this inside its HTTPS server
block (with your existing `listen 443 ssl`, hostname and certificate settings):

```nginx
location / {
    proxy_pass http://novalist-sync:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_buffering off;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
    client_max_body_size 1m;
}
```

The app trusts one proxy hop. A CDN in front of another proxy needs a separately
reviewed forwarding setup; do not trust arbitrary forwarded client addresses.
Keep proxy access logs free of credentials, invitation capabilities and request
bodies. Bearer tokens belong in the Authorization header, never query strings.
See [transport.md](transport.md) for exact forwarding and quota behavior.

## Relay with accounts

Use the same container with `Relay__RequireAuthentication=true` and an external
OIDC provider. Add these settings to your private environment file:

```dotenv
Authentication__Schemes__Bearer__Authority=https://identity.example.com/realms/novalist
Authentication__Schemes__Bearer__ValidAudiences__0=novalist-sync
Authentication__NativeClient__ClientId=novalist-desktop
Authentication__NativeClient__Scope=openid profile offline_access novalist-sync
Relay__Introspection__Endpoint=https://identity.example.com/realms/novalist/protocol/openid-connect/token/introspect
Relay__Introspection__ClientId=novalist-relay
Relay__Introspection__ClientSecret=REPLACE_WITH_SERVER_CLIENT_SECRET
```

Replace every provider-specific value, including scopes and the introspection
URL. Configure the provider to issue JWT access tokens with the configured API
audience. Register `novalist-desktop` as a public native client using authorization
code with S256 PKCE and loopback redirects at
`http://127.0.0.1:{ephemeral-port}/callback`. The desktop receives no client secret.
Register `novalist-relay` as a separate confidential client allowed to introspect
those access tokens using HTTP Basic authentication. The provider must report
disabled accounts and revoked tokens as inactive.

The discovery endpoint should now return `authenticationRequired: true` and your
issuer, native client ID and scopes. Missing issuer/audience or introspection
settings prevent production startup. An introspection outage rejects new
connections and renewals with 503; existing connections have a bounded grace
period. See [relay.md](relay.md) for revocation timing and provider requirements.

Keep the container in Production. Development mode does not make HTTP from
remote clients acceptable and is not a deployment workaround.

## Persistent mode

Use this only with clients implementing the persistent SignalR protocol. It does
not replace the live relay expected by the current desktop integration.

Provide a PostgreSQL database (PostgreSQL 17 is used in development and CI),
durable database storage, backups, and an external OIDC provider. Reuse the TLS
setup and OIDC/native-client settings above, remove relay introspection settings,
and configure:

```dotenv
Relay__Enabled=false
ConnectionStrings__Default=Host=db.example.com;Port=5432;Database=novalist_sync;Username=novalist;Password=REPLACE_WITH_DATABASE_PASSWORD;SSL Mode=VerifyFull
Collaboration__ServerUrl=https://sync.example.com/
```

Use a database certificate trusted by the container, or mount the required CA and
configure Npgsql's root certificate setting. The canonical server URL must match
the desktop's public HTTPS endpoint. It binds device signatures to this server;
changing it requires clients to select the new endpoint.

Apply migrations **before** starting the server. Startup does not migrate the
database, and the runtime image contains neither the SDK nor `dotnet-ef`. For a
complete stack, the [persistent Compose example](../deploy/README.md#example-2-persistent-server)
runs its `migrate` service and waits for success before starting the server.

For a standalone container deployment, save the persistent-mode settings above
in a private `persistent.env` file, including `ConnectionStrings__Default` for the
database being upgraded. Stop the old server, then run the matching release bundle:

```bash
docker run --rm \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --cap-drop ALL --security-opt no-new-privileges=true \
  --env-file /absolute/path/persistent.env \
  "ghcr.io/qf0xb/novalist-sync-server-migrations:$VERSION"
```

The database hostname must be reachable from this container. Add the appropriate
Docker network if the database is private to another container network. Mount any
private database CA at the path configured in the connection string, just as for
the server. Start the new server only after the bundle exits with code 0.

Alternatively, from a source checkout at the same release tag, use the
repository's development shell:

```bash
git checkout "v$VERSION"
devenv shell -- bash -c 'read -rsp "Production connection string: " ConnectionStrings__Default; echo; export ConnectionStrings__Default; dotnet ef database update --project src/Novalist.Sync.Server'
```

Supply the production connection string at the prompt; the development shell's
default points at its local database. Use a migration account with schema
permissions and a runtime account with the necessary data-access permissions.
Do not start the old server while migrating. Read [migrations.md](migrations.md)
for upgrade compatibility and rollback restrictions.

All durable server data, including encrypted blob chunks, lives in PostgreSQL.
The app needs no project-file volume. Back up the whole database, including
migration history, receipts, device records and key grants. Server backups cannot
replace client-held encryption/recovery keys. Configure first-device MFA enrollment
as described in [device-enrollment.md](device-enrollment.md).

Run exactly one instance. Live broadcasts, revocation barriers, relay sessions and
quotas are process-local; multiple replicas are unsupported, even with a shared
database or sticky sessions.

## Limits and operations

Start with the default [transport limits](transport.md) and [relay quotas](relay.md).
Clients behind one NAT share an IP quota. For persistent storage,
`BlobStorage__MaxProjectBytes` defaults to 1 GiB of encrypted blob bytes per project.
`BlobStorage__UnreferencedGracePeriod` and
`UpdateHistory__CompactionGracePeriod` default to `30.00:00:00` (30 days).
Compaction and blob collection permanently delete eligible ciphertext; see
[design.md](design.md#84-catch-up-snapshots-and-retention).

For upgrades, read the target release notes, pull its exact version, stop the old
container, and recreate it with the same environment and mounts. In persistent
mode, back up PostgreSQL and apply that release's migrations before starting it.
Verify discovery and a real client connection after each upgrade. Keep the previous
image digest and deployment settings. Relay rollback recreates the previous image;
persistent rollback may require restoring the matching database backup, especially
after compaction or blob collection. Do not assume an older image supports a newer
schema. Never run old and new containers side by side.

Common failures:

- `400 Use HTTPS.`: TLS is absent, or the proxy's source IP/forwarded headers do
  not match the trusted-proxy configuration.
- Discovery returns 503: native-client authority, client ID or scopes are missing
  or invalid. Scopes must include `openid`.
- WebSocket upgrade fails behind a proxy: check HTTP/1.1 upgrade headers and
  upstream routing. Use the hostname root, not an unconfigured URL path prefix.
- Authenticated relay returns 401/503: check audience, issuer, token expiry and
  provider introspection credentials/reachability.
- 429/503 under load: inspect the documented IP/account/instance limits before
  increasing them. A 503 can also mean the introspection provider is unavailable.
- TLS startup fails: ensure UID 1654 can read the mounted certificate and key.
- Persistent operations fail after startup: verify database reachability and that
  all migrations from the running release have been applied.

## Build an image from a release tag

From a clean checkout of an existing release tag:

```bash
git checkout "v$VERSION"
test "$(git describe --tags --exact-match)" = "v$VERSION"
docker build --pull \
  --build-arg VERSION="$VERSION" \
  --build-arg REVISION="$(git rev-parse HEAD)" \
  -t "novalist-sync-server:$VERSION" .

# For persistent mode, also build the matching migration image:
docker build --pull --target migrations \
  --build-arg VERSION="$VERSION" \
  --build-arg REVISION="$(git rev-parse HEAD)" \
  -t "novalist-sync-server-migrations:$VERSION" .
```

The multi-stage build uses the .NET 10 SDK and ships only published app files in
the ASP.NET Core runtime image. Build arguments contain public version metadata,
never credentials. Secret environment files, certificates, local build output and
development state are excluded from the build context. Rebuilding a tag can pick up newer
.NET base-image patches; retain the resulting image digest when reproducibility
matters. Modified hosted versions must satisfy the server's AGPL source-sharing
requirements; see [LICENSE](../LICENSE).
