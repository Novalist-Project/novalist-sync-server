# Relay hosting behind an existing Traefik

`compose.relay.traefik.yml` is a complete authenticated relay stack: Novalist,
Keycloak and identity PostgreSQL. It joins your existing Traefik network and
publishes no host ports. Traefik remains separately managed. Project data stays
on the hosting desktop; the relay has no project database or migration job.

Default routes:

- `https://novalist-relay.qf0xb.de/` → relay port 8080.
- `https://novalist-relay-auth.qf0xb.de/` → Keycloak port 8080.

The relay and persistent examples are standalone stacks with separate Keycloak
instances, so their templates use different authentication hostnames
(`novalist-relay-auth` and `novalist-persist-auth`). Both stacks can run on
the same Traefik at once. Never point both stacks' `OIDC_DOMAIN` at the same
hostname. The relay's router and service names are distinct from the persistent
example's. Accounts are not shared: create users in each stack's realm.

## Configure

Run commands from the repository root. If you deploy on another host, copy the
whole `deploy/` directory, including `deploy/keycloak/`. Keycloak imports
`deploy/keycloak/novalist-realm.json`, which configures the public PKCE client,
API audiences and confidential introspection client. Check that it is a regular
file before the first start; if it is missing, Docker creates a directory in its
place and Keycloak fails with `Is a directory` (see the
[fix](traefik.md#keycloak-import-is-a-directory), using this file name):

```bash
test -f deploy/keycloak/novalist-realm.json
```

```bash
cp deploy/.env.relay.traefik.example deploy/relay.env
chmod 600 deploy/relay.env
docker inspect traefik --format '{{range $name, $network := .NetworkSettings.Networks}}{{println $name $network.IPAddress}}{{end}}'
```

Edit `deploy/relay.env`:

- Set `TRAEFIK_NETWORK` to the existing network shared with Traefik.
- Set `TRAEFIK_IP` to Traefik's stable IPv4 address on that network, not a network
  CIDR, Docker gateway or its address on another network. The relay and Keycloak
  trust only this address. Pin it with a fixed `ipv4_address` in Traefik's own
  Compose file.
- Match your existing HTTPS entrypoint and certificate resolver. Leave the
  resolver empty only if Traefik already loads the required certificates.
- Generate three independent secrets with `openssl rand -hex 32`, and fill in
  `POSTGRES_PASSWORD`, `KEYCLOAK_ADMIN_PASSWORD` and `RELAY_CLIENT_SECRET`.
- Point both public hostnames at Traefik, either directly (**DNS only**) or
  through Cloudflare (**proxied**). Both work; the trade-offs and the Cloudflare
  settings are in [DNS and Cloudflare](traefik.md#dns-and-cloudflare).

The relay fetches the issuer's signing keys and calls token introspection at
`https://${OIDC_DOMAIN}` through public DNS, so it needs outbound HTTPS and a
publicly trusted certificate on that hostname. To send these requests straight to
Traefik instead, use the [direct identity routing override](traefik.md#optional-direct-identity-routing);
it requires Traefik to hold a publicly trusted certificate for `OIDC_DOMAIN`.
Without one, the relay rejects every token with 401.

The TLS-only relay router uses a per-router middleware to set
`X-Forwarded-Proto: https`. Traefik otherwise uses `wss` for WebSocket upgrades,
which ASP.NET Core does not recognize as an HTTPS request. This normalization is
limited to the TLS router; the server still checks the trusted proxy address and
rejects plain HTTP.

## Start and verify

```bash
docker compose --env-file deploy/relay.env -f deploy/compose.relay.traefik.yml config --quiet
docker compose --env-file deploy/relay.env -f deploy/compose.relay.traefik.yml pull
docker compose --env-file deploy/relay.env -f deploy/compose.relay.traefik.yml up -d
docker compose --env-file deploy/relay.env -f deploy/compose.relay.traefik.yml ps

curl --fail https://novalist-relay-auth.qf0xb.de/realms/novalist/.well-known/openid-configuration
curl --fail https://novalist-relay.qf0xb.de/client-configuration
curl --silent --output /dev/null --write-out '%{http_code}\n' \
  https://novalist-relay.qf0xb.de/relay/v1/host
```

Run these without `-k`. The `issuer` must exactly match the `authority` from
`/client-configuration`, and the last command must print `401`. With a valid
access token, the same request returns `400` because it is not a WebSocket, and
a WebSocket upgrade returns `101`. A `401` with a valid token means the relay
cannot validate tokens; see [Tokens rejected with 401](traefik.md#tokens-rejected-with-401).

Open `https://novalist-relay-auth.qf0xb.de/admin/` with the bootstrap administrator.
Create a permanent administrator and collaborators in the `novalist` realm as
described in [the deployment guide](README.md#create-user-accounts). Use
`https://novalist-relay.qf0xb.de/` as the desktop's relay server URL. Sign in, share
a project and join from another desktop. The hosting desktop must stay online.

Disabling an account or revoking its access token must cause introspection to
reject new connections and end existing sessions as documented in
[relay.md](../docs/relay.md#token-status-and-provider-configuration).

If `TRAEFIK_IP` changes, update the environment file and recreate the two consumers:

```bash
docker compose --env-file deploy/relay.env -f deploy/compose.relay.traefik.yml \
  up -d --force-recreate server keycloak
```

For `400 Use HTTPS.`, follow the [proxy diagnostics](traefik.md#400-use-https).
Never disable production HTTPS validation to work around rejected forwarding.
Apply the same [hardening](traefik.md#hardening) as the persistent example,
especially blocking the Keycloak admin console publicly.

## Operations

Back up the identity PostgreSQL database and private environment file. Existing
realms skip import: rotating the introspection secret requires updating both
Keycloak and `relay.env`. Changing the PostgreSQL password in an environment file
does not change an existing database role's password.

For updates, change the exact release version, pull and recreate this stack.
Restarting the relay ends live sharing sessions and requires fresh invitations.
Removing this Compose project leaves the external Traefik network and deployment
intact. Do not remove named volumes during upgrades; they contain the accounts.
