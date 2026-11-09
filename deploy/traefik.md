# Persistent hosting behind an existing Traefik

Use `compose.persistent.traefik.yml` as the complete Compose entry point. It starts
the persistent server, migration job, project database, Keycloak and identity
database. It neither creates nor manages Traefik or Caddy, and publishes no host
ports. The existing Traefik must run on the same Docker host, use its Docker
provider, and already serve HTTPS on the external Docker network you select.

Routes:

- `https://novalist-persist.qf0xb.de/` → Novalist server port 8080.
- `https://novalist-persist-auth.qf0xb.de/` → Keycloak port 8080.

## Prepare

Run these commands from the repository root. If you deploy on another host, copy
the whole `deploy/` directory, including `deploy/keycloak/`, not just the Compose
and environment files. Keycloak imports
`deploy/keycloak/novalist-persistent-realm.json`, which configures PKCE and the
required password-plus-OTP enrollment flow. Check that it is a regular file before
the first start:

```bash
test -f deploy/keycloak/novalist-persistent-realm.json
```

If the file is missing when the stack first starts, Docker creates an empty
directory in its place and Keycloak fails with
`novalist-realm.json (Is a directory)`. See [Troubleshooting](#keycloak-import-is-a-directory).

Find Traefik's existing network and IP address, replacing `traefik` if its
container has another name:

```bash
docker inspect traefik --format '{{range $name, $network := .NetworkSettings.Networks}}{{println $name $network.IPAddress}}{{end}}'
```

Use the actual external network name, including any Compose project prefix, and
the address Traefik has on that network. Traefik connected to several networks has
several addresses; only the one on `TRAEFIK_NETWORK` counts. Novalist and Keycloak
trust that exact address. Give Traefik a fixed `ipv4_address` on that network in
its own Compose file if it does not have one already: otherwise the address can
change when Traefik restarts. If the address changes, update `TRAEFIK_IP` and
recreate Novalist and Keycloak.

Copy the environment template, then edit it:

```bash
cp deploy/.env.traefik.example deploy/.env
chmod 600 deploy/.env
```

- Fill `TRAEFIK_NETWORK` and `TRAEFIK_IP` from the inspection output.
- Set `TRAEFIK_HTTPS_ENTRYPOINT` and `TRAEFIK_CERT_RESOLVER` to your existing
  entrypoint and certificate resolver names. Leave the resolver empty if Traefik
  already loads the appropriate certificates without ACME.
- Generate independent `POSTGRES_PASSWORD`, `SYNC_DB_PASSWORD` and
  `KEYCLOAK_ADMIN_PASSWORD` values with `openssl rand -hex 32`.
- Use the template's `NOVALIST_VERSION=1.0.0`, or another published release with
  a matching migration image. Change both domain settings if deploying under
  different hostnames.
- Point both public DNS names at the Traefik host, either directly or through
  Cloudflare. See [DNS and Cloudflare](#dns-and-cloudflare).

The server fetches the identity provider's discovery document and signing keys
from `https://${OIDC_DOMAIN}` through public DNS, the same way the desktop app
does. The server container therefore needs outbound HTTPS access, and the
certificate it receives for `OIDC_DOMAIN` must be publicly trusted.

### DNS and Cloudflare

Both of these setups work. Pick one for both hostnames.

**DNS only.** Clients connect directly to Traefik, which needs a publicly trusted
certificate for both hostnames from `TRAEFIK_CERT_RESOLVER`. The server reaches
Keycloak through the host's public address. If the host is behind NAT, the router
must support hairpin NAT, or the server cannot reach the identity provider; use
[direct identity routing](#optional-direct-identity-routing) in that case.

**Cloudflare proxied (orange cloud).** Cloudflare terminates public TLS. Set the
zone's SSL/TLS mode to **Full**, or **Full (strict)** if Traefik holds publicly
trusted certificates. WebSockets are enabled by default and must stay enabled.
Cloudflare's edge connects to Traefik; Traefik replaces the forwarded-header chain
with Cloudflare's address because it does not trust Cloudflare, so Novalist still
sees exactly one trusted hop. Novalist's per-address request limits then apply per
Cloudflare edge address, not per visitor; authenticated requests are still limited
per account. Cloudflare's free plan limits request bodies to 100 MB.

Behind Cloudflare, also [restrict the origin](#hardening) so that clients cannot
bypass Cloudflare by connecting to the host address directly.

### Optional: direct identity routing

The server can send its identity-provider requests straight to Traefik instead of
through public DNS. This keeps that traffic on the host and avoids hairpin NAT.
It only works when Traefik itself holds a publicly trusted certificate for
`OIDC_DOMAIN`: Traefik's default certificate fails validation, and every token is
then rejected with 401. A Cloudflare Origin CA certificate is not enough either,
since only Cloudflare trusts it. Add this as `deploy/compose.override.yml` and
pass it after the main file with `-f`:

```yaml
services:
  server:
    extra_hosts:
      - "${OIDC_DOMAIN}=${TRAEFIK_IP}"
```

Check Traefik's certificate before enabling it; the command must succeed without
`-k`:

```bash
curl --fail --resolve "novalist-persist-auth.qf0xb.de:443:<traefik-host-ip>" \
  https://novalist-persist-auth.qf0xb.de/realms/novalist/.well-known/openid-configuration
```

## Start

```bash
docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml config --quiet
docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml pull
docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml up -d
docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml ps --all
```

Traefik discovers the two host routers from labels on `server` and `keycloak`.
The explicit `traefik.docker.network` labels select the shared proxy network
instead of either private database network. WebSocket upgrades work automatically.
The TLS-only server router normalizes `X-Forwarded-Proto` to `https`, including
WebSockets: Traefik's default `wss` value otherwise fails ASP.NET Core's HTTPS
check. This middleware is scoped to the TLS router, not applied globally.
The databases have separate internal networks and persistent volumes. The server
waits for Keycloak health and successful database migrations before starting.

If replacing the Caddy-based persistent example, stop and remove its `proxy`
service using the old Compose file before starting this example. Both examples
use project name `novalist-persistent` and the same database volume names, so the
new file reuses existing data. Do not use `down --volumes`. Existing realms skip
import; changing proxy configuration does not reset users or their OTP devices.

## Verify and sign in

```bash
curl --fail https://novalist-persist-auth.qf0xb.de/realms/novalist/.well-known/openid-configuration
curl --fail https://novalist-persist.qf0xb.de/client-configuration
curl --silent --output /dev/null --write-out '%{http_code}\n' \
  --request POST https://novalist-persist.qf0xb.de/sync/negotiate
```

Run these without `-k`. The first two must print JSON. The `issuer` in the first
must exactly match the `authority` in the second. The third must print `401`: the
sync hub is reachable and requires a token. A `404` from the root URL
`https://novalist-persist.qf0xb.de/` is expected; the server has no page there.

Open `https://novalist-persist-auth.qf0xb.de/admin/` using the bootstrap
administrator. Create a permanent administrator and ordinary users as described in
[the deployment guide](README.md#create-user-accounts). Ordinary users belong in
the `novalist` realm and must configure OTP before verified-device enrollment.
Use `https://novalist-persist.qf0xb.de/` in a compatible persistent-protocol client.
After signing in, the client's device registration must succeed. If it fails with
401 although the login worked, see [Tokens rejected with 401](#tokens-rejected-with-401).

Follow the [backup and upgrade instructions](README.md#backups-and-upgrades),
passing `-f deploy/compose.persistent.traefik.yml` for every operation. The dashboard
and other services remain managed by your existing Traefik deployment.

## Hardening

- **Origin lock-down (Cloudflare proxied).** Allow ports 80 and 443 only from
  [Cloudflare's IP ranges](https://www.cloudflare.com/ips/) in the host firewall,
  or require Authenticated Origin Pulls in Traefik. Otherwise anyone who knows the
  host address can reach Novalist and Keycloak without Cloudflare's protection.
- **Keycloak admin console.** This example publishes the whole Keycloak hostname,
  including `/admin` and the `master` realm. Block both publicly, for example with
  a Cloudflare WAF rule for `/admin` and `/realms/master` on `OIDC_DOMAIN`, and
  reach the console through an SSH tunnel to the host when you need it. Novalist
  clients only need `/realms/novalist` and `/resources`.
- **HSTS.** If Cloudflare sends `Strict-Transport-Security`, use a real
  `max-age` (at least six months) or turn HSTS off. A header with `max-age=0`
  disables HSTS.
- **Host ports.** This stack publishes none. Check that nothing else on the host
  publishes database or management ports to the internet.

## Troubleshooting

For startup problems, inspect this stack's `server`, `migrate` and `keycloak` logs
and the existing Traefik logs. A 404 from Traefik usually means the routers were
not discovered or use the wrong entrypoint; a 502 means the proxy network or
backend port is wrong. Check any Docker-provider constraints already configured in
shared Traefik if it ignores these labeled services.

### Keycloak import: Is a directory

`ERROR: /opt/keycloak/bin/../data/import/novalist-realm.json (Is a directory)` means
the realm file was missing when the container was first created, so Docker created
a directory at its path. Remove that directory, copy the real file, and recreate
Keycloak; a plain `up` may keep the old mount:

```bash
rm -r deploy/keycloak/novalist-persistent-realm.json
# copy deploy/keycloak/novalist-persistent-realm.json from the release checkout
docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml \
  up -d --force-recreate keycloak
```

Relative paths in the Compose file resolve against the directory of the first
`-f` file, so run Compose with the same file paths every time.

### Certificate does not match the hostname

If `curl` without `-k` reports that no certificate name matches the hostname,
Traefik is serving its default certificate. With DNS only, check that
`TRAEFIK_CERT_RESOLVER` names a resolver from Traefik's static configuration and
look for ACME errors in Traefik's logs. With Cloudflare, check that the record is
proxied; resolve the name through a public resolver, since a local DNS cache can
still return the origin address after you switch.

### Tokens rejected with 401

The login works and the access token looks correct, but every authenticated
request returns 401 with `WWW-Authenticate: Bearer error="invalid_token"`. The
server cannot fetch the identity provider's signing keys. Look for the cause in
the server log:

```bash
docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml \
  logs server 2>&1 | grep -iE 'IDX20803|IDX10500|certificate|SSL' | tail -5
```

- A certificate error while [direct identity routing](#optional-direct-identity-routing)
  is enabled means Traefik has no publicly trusted certificate for `OIDC_DOMAIN`.
  Remove the override and recreate the server.
- A connection timeout without the override usually means the host cannot reach
  its own public address (missing hairpin NAT) or the container has no outbound
  access.

Also check that the token's `iss` claim exactly matches the `authority` returned
by `/client-configuration`, and that its `aud` contains `novalist-sync`.

### 400 Use HTTPS.

This response comes from Novalist when the request is still HTTP after trusted
forwarding is processed. The public connection may still have used HTTPS.

Check these settings in order:

1. Use an `https://` URL and Traefik's TLS-enabled router.
2. Compare `TRAEFIK_IP` with Traefik's actual address on the selected proxy network.
   Inspect the effective application setting without printing credentials:

   ```bash
   docker compose --env-file deploy/.env -f deploy/compose.persistent.traefik.yml \
     exec -T server printenv Transport__KnownProxies__0
   docker inspect traefik --format '{{range $name, $network := .NetworkSettings.Networks}}{{println $name $network.IPAddress}}{{end}}'
   ```

3. If Traefik's entrypoint trusts incoming forwarded headers
   (`forwardedHeaders.trustedIPs` or `forwardedHeaders.insecure`), it passes on
   the client's `X-Forwarded-For` chain and adds to it. Novalist then receives more
   `X-Forwarded-For` entries than `X-Forwarded-Proto` entries and ignores both.
   Remove that trust for this entrypoint, or use a separate entrypoint.
4. If discovery works but WebSocket connections return `Use HTTPS.`, check that
   the TLS router uses the supplied HTTPS-header normalization middleware.

If the response persists, temporarily enable only the forwarded-header logger
through a Compose override on `server`:

```yaml
services:
  server:
    environment:
      Logging__LogLevel__Microsoft.AspNetCore.HttpOverrides: Debug
```

Recreate the server with that override and repeat one discovery request. Inspect
the server logs for an unknown proxy address or a forwarded-header parameter-count
mismatch. This logger diagnoses the forwarding metadata without enabling request
body logging. Remove the diagnostic override after collecting the result.
