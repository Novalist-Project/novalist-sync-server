# Production Compose deployment

Choose one of two production examples:

- **Relay:** `compose.relay.yml` hosts the current desktop integration. The hosting
  desktop must remain online. PostgreSQL stores Keycloak accounts and sessions;
  the relay stores no project content.
- **Persistent server:** `compose.persistent.yml` hosts the separate SignalR
  protocol. It adds a dedicated project PostgreSQL instance, an automatic migration
  job, the canonical server URL, and a Keycloak password-plus-OTP enrollment flow.

Both examples reuse `compose.common.yml` for Caddy, OIDC and identity storage.
They are complete entry points: pass only the chosen example, not the common file,
to Compose. Development still uses `devenv`.

For a persistent server behind a separately managed, shared Traefik, use the
[standalone Traefik example](traefik.md) instead. It includes all Novalist services
without starting a proxy or publishing ports.

The [standalone relay Traefik example](traefik-relay.md) provides the equivalent
setup for live relay hosting.

Unlike the Caddy examples, both Traefik examples also work behind Cloudflare's
proxy; see [DNS and Cloudflare](traefik.md#dns-and-cloudflare).

## Requirements

- Docker Engine with the Compose plugin on a Linux AMD64 or ARM64 host.
- At least 2 GiB available for Keycloak, plus memory for PostgreSQL, Caddy and relay
  traffic; 4 GiB total is a practical starting point for a small deployment.
- Two public DNS names pointing directly at this host, for example
  `sync.example.com` and `identity.example.com`. Any AAAA records must also route
  correctly. TCP ports 80 and 443 must reach Caddy for automatic certificate issuance.
- A published Novalist server release containing these deployment files.

Run all commands below from the root of the matching release checkout. If another
proxy already owns ports 80/443, stop it or integrate these upstreams into that
proxy instead. Do not place a CDN or another proxy in front of this configuration.

## Configure and start

Copy the environment template and restrict its permissions:

```bash
cp deploy/.env.example deploy/.env
chmod 600 deploy/.env
```

Edit `deploy/.env`:

- Set `NOVALIST_VERSION` to an existing release without its leading `v`, such as
  `1.0.0` or `1.0.0-rc.1`. There are no `latest` or branch image tags.
- Set the two hostnames and ACME contact email.
- Choose an unused `FRONTEND_SUBNET` and set `PROXY_IP` to a free address within it.
  Caddy gets this fixed address; both the relay and Keycloak trust only that proxy.
- Generate independent secrets with `openssl rand -hex 32`. Both examples need
  `POSTGRES_PASSWORD` and `KEYCLOAK_ADMIN_PASSWORD`. Relay additionally needs
  `RELAY_CLIENT_SECRET`; persistent mode needs `SYNC_DB_PASSWORD`. Hex avoids
  quoting/interpolation problems in environment files and realm import JSON.
- Review the pinned Caddy, Keycloak and PostgreSQL image releases. Keep PostgreSQL
  on major version 17 unless you perform a database major-version upgrade.

Empty required values prevent Compose from starting. The environment file is
gitignored. It contains credentials; do not publish it or the output of
`docker compose config` without `--quiet`.

### Example 1: authenticated relay

```bash
export COMPOSE_FILE=deploy/compose.relay.yml
docker compose --env-file deploy/.env config --quiet
docker compose --env-file deploy/.env pull
docker compose --env-file deploy/.env up -d
docker compose --env-file deploy/.env ps
```

This preserves the existing relay project's name, `novalist`, and its volume names.
Existing deployments using `deploy/compose.yml` should use this new entry point.
No project database or migration image is pulled in relay mode.

### Example 2: persistent server

```bash
export COMPOSE_FILE=deploy/compose.persistent.yml
docker compose --env-file deploy/.env config --quiet
docker compose --env-file deploy/.env pull
docker compose --env-file deploy/.env up -d
docker compose --env-file deploy/.env ps --all
```

The `sync-db` service stores encrypted projects and blobs in `sync_data`, separately
from Keycloak's database. The one-shot `migrate` service uses
`ghcr.io/qf0xb/novalist-sync-server-migrations:$NOVALIST_VERSION`, an EF Core bundle
built from the same release as the server. The server starts only after migrations
succeed. A nonzero migration exit blocks startup; inspect its logs before retrying.
Neither the host nor the production runtime container needs an SDK or `dotnet-ef`.

The database listener has no published port. Its connection uses plaintext only
inside the dedicated, internal, single-host Docker network. This small-host example
uses one database role for migration and runtime; use separate deployment and
runtime roles with restricted grants when adapting it to a managed database.

The persistent project name is `novalist-persistent`, so its volumes cannot silently
reuse a relay deployment's identity database and realm. Choose one example per host
and hostname pair. Changing examples does not migrate project data, accounts or
encryption keys, and does not change which protocol the desktop implements.

### Shared networking and startup

Keep the selected `COMPOSE_FILE` exported for every command below, including in a
new terminal. This selects the right services and persistent volumes.

Only Caddy publishes ports. Keycloak and the server use HTTP on the private frontend
network; PostgreSQL uses a separate internal network reachable by Keycloak only.
Caddy automatically handles WebSocket upgrades and public certificates for both
hostnames. Its default forwarding replaces untrusted client-supplied forwarding
headers. Its network alias for the identity hostname also routes the relay's
OIDC discovery and introspection requests through HTTPS internally, without relying
on router hairpin NAT. The public issuer stays identical inside and outside Docker.

Startup waits for PostgreSQL and Keycloak health checks before starting the server.
Caddy starts independently so it can obtain certificates during bootstrap.
Certificate issuance and Keycloak's first database initialization can take several
minutes. A healthy Keycloak does not prove that public DNS or certificates work.

## Create user accounts

Open `https://identity.example.com/admin/` using your configured identity hostname.
Sign in with the bootstrap administrator from `deploy/.env`.

Create a permanent administrator in the `master` realm, grant the required admin
roles, verify the new administrator works, then remove the temporary bootstrap
administrator. Keep administrator credentials separate from ordinary users.

Select the `novalist` realm. Under **Users**, create each collaborator with
**Enabled** set, then set their password under **Credentials**. A temporary
password forces a change on first login. Self-registration and password-reset
email are disabled; this template does not configure an SMTP service.

Both realm imports configure:

- `novalist-desktop`: public native client, authorization code flow with required
  S256 PKCE, exact loopback callback path `/callback`, and API audience
  `novalist-sync`. Keycloak accepts arbitrary loopback ports for the registered
  `http://127.0.0.1/callback` URI; no wildcard redirect is needed.
- Native scopes `openid profile offline_access`, with refresh tokens handled
  only by the desktop. The server receives access tokens, never refresh tokens.

The relay realm additionally configures:

- `novalist-relay`: confidential introspection client whose secret matches the
  relay configuration. Password grants, implicit flow and service accounts are
  disabled for both clients.
- An extra access-token audience, `novalist-relay`, required by Keycloak's
  introspection audience check.

Realm import runs only when the realm does not already exist. Editing the JSON
or changing `RELAY_CLIENT_SECRET` in `.env` does not update an existing realm.
For secret rotation, change the confidential client's secret in Keycloak, update
`deploy/.env` to match, and recreate the relay and Keycloak services. Do not delete
the realm or database to apply changes.

Likewise, `POSTGRES_PASSWORD` and `SYNC_DB_PASSWORD` initialize new databases;
changing `.env` does not rotate an existing database role's password. Change that
role's password in PostgreSQL before updating its matching connection credentials.

### Persistent-mode MFA

The persistent realm imports a separate browser flow requiring a password followed
by OTP for every interactive login. Users without OTP must configure it on first
login. Complete another fresh login after initial OTP setup before enrolling a
verified device.

The AMR mapper emits `mfa` only after the required OTP execution succeeds, after
the required password execution. It is not a hard-coded token claim. The built-in
`basic` client scope supplies `auth_time`; fresh enrollment uses the client's
nonce and `max_age=0`, as required by [device-enrollment.md](../docs/device-enrollment.md).
The server still rejects missing, stale or invalid enrollment proof. Do not replace
this flow with password-only login or a constant `amr` claim.

The persistent realm has no relay introspection client and needs no relay secret.

## Verify

Use the actual hostnames from your environment file:

```bash
curl --fail --show-error https://identity.example.com/realms/novalist/.well-known/openid-configuration
curl --fail --show-error https://sync.example.com/client-configuration
```

The issuer must be `https://identity.example.com/realms/novalist`. Server discovery
must advertise authentication, that issuer, and client ID `novalist-desktop`.
For relay mode, set the desktop server to `https://sync.example.com/`, sign in, then
share and join from two desktops. Test account disablement: introspection must reject
new connections and close existing ones within the interval documented in
[relay.md](../docs/relay.md#token-status-and-provider-configuration).

For persistent mode, check `migrate` exited with code 0, then test device enrollment
and a project push/catch-up with a compatible persistent-protocol client. Recreate
the server and confirm the same account and encrypted project data remain. This
protocol does not add relay endpoints, and does not use relay token introspection.

For startup failures:

```bash
docker compose --env-file deploy/.env logs --tail 100 proxy keycloak server
```

Check public DNS/port reachability for certificate failures, the Keycloak health
status for dependency failures, and matching client secrets for introspection 503s.
If the subnet conflicts with existing networks, change both network settings
before recreating the stack. Never bypass HTTPS validation to make sign-in work.

## Backups and upgrades

PostgreSQL's `postgres_data` volume contains identity configuration, accounts and
sessions. Caddy's `caddy_data` volume contains certificate private keys and renewal
state; `caddy_config` contains its saved configuration. Back up the database,
these volumes, and your private environment/deployment settings. Relay project
content stays on desktops in relay mode. Persistent mode additionally needs a full
backup of `sync-db`, including migration history, grants, receipts and encrypted
blobs. Server backups do not replace client-held recovery/encryption keys.

For consistent database dumps, pause identity changes and stop Keycloak and the
server while PostgreSQL remains running. Store dumps outside the checkout in a
private backup directory:

```bash
docker compose --env-file deploy/.env stop server keycloak
umask 077
docker compose --env-file deploy/.env exec -T postgres \
  pg_dump -U keycloak -d keycloak -Fc > /absolute/private/backup/keycloak.dump
# Persistent example only:
docker compose --env-file deploy/.env exec -T sync-db \
  pg_dump -U novalist -d novalist_sync -Fc > /absolute/private/backup/novalist_sync.dump
docker compose --env-file deploy/.env up -d
```

For upgrades, back up first, read all affected release notes, update exact image
versions in `.env`, pull them, and recreate the stack. Keycloak applies its own
database migrations on startup. Restore a matching pre-upgrade database backup
before rolling back across incompatible Keycloak versions. PostgreSQL major
upgrades require `pg_upgrade` or dump/restore; changing the major image tag alone
does not migrate its data directory. Persistent schema rollback can require restoring
the matching project database backup; see [migrations.md](../docs/migrations.md).

For a persistent upgrade, stop the old server before running any new migration:

```bash
docker compose --env-file deploy/.env stop server
# Back up both databases, update .env, then:
docker compose --env-file deploy/.env pull
docker compose --env-file deploy/.env rm -f migrate
docker compose --env-file deploy/.env up -d
```

Removing the exited migration container ensures the new bundle runs even when
retrying the same release. The server's dependency waits for that job to succeed.
Never run the old server against a newly migrated schema.

Keep each example's Compose project name stable so upgrades reuse the same volumes.
Run one server replica. Restarting a relay ends live sessions and requires fresh
invitations. `docker compose down` preserves named volumes; **`down --volumes`
deletes the identity database, project database and certificate state**. Do not use
it for upgrades.
