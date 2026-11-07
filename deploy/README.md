# Production Compose deployment

This stack hosts the current desktop's **authenticated relay** using Caddy for
public HTTPS, Keycloak for OIDC, and PostgreSQL for Keycloak's accounts and sessions.
It does not enable the separate persistent SignalR protocol or store project
updates. The hosting desktop must remain online. Development still uses `devenv`.

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
- Generate three independent secrets with `openssl rand -hex 32`, and fill in
  `POSTGRES_PASSWORD`, `KEYCLOAK_ADMIN_PASSWORD` and `RELAY_CLIENT_SECRET`. Hex avoids
  quoting/interpolation problems in environment files and realm import JSON.
- Review the pinned Caddy, Keycloak and PostgreSQL image releases. Keep PostgreSQL
  on major version 17 unless you perform a database major-version upgrade.

Empty required values prevent Compose from starting. The environment file is
gitignored. It contains credentials; do not publish it or the output of
`docker compose config` without `--quiet`.

```bash
docker compose --env-file deploy/.env -f deploy/compose.yml config --quiet
docker compose --env-file deploy/.env -f deploy/compose.yml pull
docker compose --env-file deploy/.env -f deploy/compose.yml up -d
docker compose --env-file deploy/.env -f deploy/compose.yml ps
```

Only Caddy publishes ports. Keycloak and the relay use HTTP on the private frontend
network; PostgreSQL uses a separate internal network reachable by Keycloak only.
Caddy automatically handles WebSocket upgrades and public certificates for both
hostnames. Its default forwarding replaces untrusted client-supplied forwarding
headers. Its network alias for the identity hostname also routes the relay's
OIDC discovery and introspection requests through HTTPS internally, without relying
on router hairpin NAT. The public issuer stays identical inside and outside Docker.

Startup waits for PostgreSQL and Keycloak health checks before starting the relay.
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

The initial realm import configures:

- `novalist-desktop`: public native client, authorization code flow with required
  S256 PKCE, exact loopback callback path `/callback`, and API audience
  `novalist-sync` plus introspection audience `novalist-relay`. Keycloak accepts
  arbitrary loopback ports for the registered
  `http://127.0.0.1/callback` URI; no wildcard redirect is needed.
- `novalist-relay`: confidential introspection client whose secret matches the
  relay configuration. Password grants, implicit flow and service accounts are
  disabled for both clients.
- Native scopes `openid profile offline_access`, with refresh tokens handled
  only by the desktop. The relay receives access tokens, never refresh tokens.

Realm import runs only when the realm does not already exist. Editing the JSON
or changing `RELAY_CLIENT_SECRET` in `.env` does not update an existing realm.
For secret rotation, change the confidential client's secret in Keycloak, update
`deploy/.env` to match, and recreate the relay and Keycloak services. Do not delete
the realm or database to apply changes.

Likewise, `POSTGRES_PASSWORD` initializes a new database; changing `.env` does not
rotate an existing database role's password. Change that role's password in
PostgreSQL before updating Keycloak's matching connection credential.

This realm supports relay sign-in. Persistent-mode verified-device enrollment
additionally needs a real MFA policy and truthful `amr`/`auth_time` ID-token claims;
the template does not claim a password login is MFA.

## Verify

Use the actual hostnames from your environment file:

```bash
curl --fail --show-error https://identity.example.com/realms/novalist/.well-known/openid-configuration
curl --fail --show-error https://sync.example.com/client-configuration
```

The issuer must be `https://identity.example.com/realms/novalist`. Relay discovery
must advertise authentication, that issuer, and client ID `novalist-desktop`.
Set the desktop server to `https://sync.example.com/`, sign in, then share and join
from two desktops. Test account disablement in Keycloak: introspection must reject
new connections and close existing ones within the interval documented in
[relay.md](../docs/relay.md#token-status-and-provider-configuration).

For startup failures:

```bash
docker compose --env-file deploy/.env -f deploy/compose.yml logs --tail 100 proxy keycloak server
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
content stays on desktops; this stack has no project-data volume.

For a consistent database dump, pause identity changes and stop Keycloak and the
relay while PostgreSQL remains running. Store the dump outside the checkout in a
private backup directory:

```bash
docker compose --env-file deploy/.env -f deploy/compose.yml stop server keycloak
umask 077
docker compose --env-file deploy/.env -f deploy/compose.yml exec -T postgres \
  pg_dump -U keycloak -d keycloak -Fc > /absolute/private/backup/keycloak.dump
docker compose --env-file deploy/.env -f deploy/compose.yml up -d
```

For upgrades, back up first, read all affected release notes, update exact image
versions in `.env`, pull them, and recreate the stack. Keycloak applies its own
database migrations on startup. Restore a matching pre-upgrade database backup
before rolling back across incompatible Keycloak versions. PostgreSQL major
upgrades require `pg_upgrade` or dump/restore; changing the major image tag alone
does not migrate its data directory.

Keep the Compose project name `novalist` stable so upgrades reuse the same volumes.
Run one relay replica. Restarting it ends live sharing sessions and requires fresh
invitations. `docker compose down` preserves named volumes; **`down --volumes`
deletes the identity database and certificate state**. Do not use it for upgrades.
