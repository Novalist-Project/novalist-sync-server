# Verified-device enrollment

Persistent mode accepts first-device enrollment after a fresh browser MFA login.
This flow works with bearer access tokens: it does not require the provider to
issue access-token `cnf.jkt` claims. The ordinary device and recovery device must
have independent Ed25519 signing and X25519 receiving keys.

1. The signed-in client requests `/devices/registration` and checks the canonical
   server URL and account ID before signing registrations.
2. It posts both public-key records to `/devices/enrollment`. The server binds a
   random, one-use nonce to that account and both complete public-key records.
   Challenges expire after five minutes, with eight per account and 1,024 total.
3. The client starts authorization-code sign-in with S256 PKCE, the returned
   nonce, `max_age=0`, and essential `amr` and `auth_time` ID-token claims.
4. It submits its ordinary and recovery possession signatures, the recovery
   linking signature, and `EnrollmentProof { challengeId, identityToken }` to
   `/devices/verified`, authenticated with its current API access token.
5. The server independently verifies the ID token against the configured
   provider's signing keys, issuer, native-client audience, account subject,
   nonce, expiry, and recent MFA. It exclusively claims the challenge while
   registering both devices atomically. Successful registration consumes the
   claim; a validation or database failure releases it for retry before expiry.

## Key-bound enrollment in one sign-in

The challenge flow needs a signed-in client, so a new device signs in twice. When
`/client-configuration` reports `keyBoundEnrollment: true` (persistent servers
with `Collaboration__ServerUrl` configured), the client can sign in and enroll
with one browser login instead:

1. Before signing in, the client creates its ordinary and recovery identities and
   a random 32-byte salt, and computes the nonce with
   `DeviceProof.EnrollmentNonce(serverUrl, keys, recoveryKeys, salt)`: unpadded
   base64url SHA-256 over a fixed domain, the length-prefixed canonical server
   URL, both public-key records and the salt. The account ID is not known yet, so
   it is not part of the nonce.
2. It signs in once with that nonce, `max_age=0`, and essential `amr` and
   `auth_time` claims, keeping the access token, refresh token and ID token.
3. It requests `/devices/registration`, signs its registrations for the returned
   account, and submits `EnrollmentProof { challengeId: empty, identityToken, salt }`
   to `/devices/verified` with the access token from the same sign-in.
4. The server recomputes the nonce from the submitted keys and salt, then runs
   the same ID-token checks as the challenge flow. The ID token's subject must
   match the access token's, which binds the account. Each nonce is accepted once:
   a successful registration keeps it as a tombstone for six minutes, past the
   five-minute MFA window; a rejected attempt releases it. Key-bound claims share
   the challenge limits.

Substituting either key record changes the nonce, so a captured proof cannot
enroll other keys. Clients fall back to the challenge flow when the server does
not advertise key-bound enrollment, or when the device already has identities
the sign-in nonce was not derived from.

## What the server learns

The server receives public keys, the device association, signatures, and the
short-lived identity proof. It stores no ID token, refresh token, recovery
secret, private key, or project key. Challenges are in memory and do not survive
a restart. A replayed proof is rejected; after a lost successful response, the
client can retry ordinary registration without the consumed enrollment proof.

Configure `Collaboration__ServerUrl` to the public HTTPS base URL and
`Authentication__NativeClient__ClientId` to the public desktop client.
Set `Authentication__NativeClient__Scope` to include `openid` and the provider's
API scopes; add `offline_access` when refresh tokens are supported. Configure
the existing Bearer authority/audience settings for API access. Device HTTP
endpoints return 503 until a valid canonical server URL is configured. The provider
must enforce MFA for that client's fresh authentication flow and issue truthful
`amr` (including `mfa`) and `auth_time` in its signed ID token. Enrollment rejects
missing or stale MFA claims; it does not infer MFA from a password login or an
arbitrary `acr` value. Browser reauthentication is separate from normal access
token refresh. Provider policy is responsible for requiring the factors again.

The desktop library's `HostedDeviceEnrollment.EnrollAsync` overloads accepting a
`HostedAccount` and browser callback/native browser perform this whole flow.
Callers must first save or show the recovery secret and supply its derived
identity. These are persistent-mode APIs; relay sessions use peer authentication
and do not require persistent verified-device registration.

Later devices require a signature from an active verified device or the saved
recovery identity. Each identity has distinct signing and receiving keys;
revoked identities retain their keys to prevent re-enrollment under another ID.
Losing every ordinary device does not reopen bearer-only
enrollment, and a revoked recovery identity cannot authorize a replacement.
Recovery of project content still requires its encryption keys.

## Lost-device identity reset

Persistent servers with verified-device enrollment configured advertise
`accountIdentityReset: true` in `/client-configuration`. A signed-in user who has
lost every authorized device and the recovery credentials can explicitly forfeit
all previous project access and start with a new internal identity:

1. POST `/account/reset/challenge` with the account's Bearer token. The server
   returns the usual challenge shape (`id`, `nonce`, `expiresAt`), with a separate
   reset purpose. Challenges expire after five minutes and share enrollment's
   bounded per-account and per-instance capacity.
2. Reauthenticate in the browser with `prompt=login`, `max_age=0` and that nonce.
   The provider-signed ID token must match the access token's issuer and subject,
   the native client audience and nonce, and carry fresh `auth_time` and MFA.
3. POST `/account/reset` with `{ challengeId, identityToken,
   forfeitProjectAccess: true }` and the Bearer token. Enrollment challenges and
   reset challenges are not interchangeable. False or missing confirmation is
   rejected. Proofs cannot be replayed after a successful reset.
4. The server serializes against account access changes and atomically archives
   the old user's login mapping under `urn:novalist:archived-account:<old-id>`,
   clears its searchable email, revokes its devices, and creates a new user for
   the original issuer and subject. It aborts the old account's live connections.
   The response is `{ userId }`. Other accounts and the identity provider are
   unchanged. No database migration or service restart is needed.
5. The desktop persists a new, server-specific local device namespace, retaining
   old credentials for local copies and other servers. Choose **Register this
   device** and complete the separate fresh-MFA enrollment. Old project access
   and ownership do not transfer; an owner must issue a new invitation.

This is identity abandonment, not cryptographic recovery. Old user/device IDs,
public keys, linking proofs, memberships, invitations and encrypted history stay
attached to the archived identity so other collaborators can retain history.
Projects with no other owner can become administratively stranded; the
confirmation explicitly includes forfeiting ownership. A failed transaction
retains the original account and devices and allows its pending proof to retry.
Logs record only the archived and replacement account IDs, never identity tokens.

The earlier `cnf.jkt` enrollment path remains available for callers that already
obtain a provider-signed, key-bound MFA access token. It is not the browser flow
and does not implement the full DPoP HTTP protocol.

Protocol references: [OIDC nonce, auth_time and max_age](https://openid.net/specs/openid-connect-core-1_0.html)
and [authentik key binding](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/key-binding/).
authentik's documented key binding applies to ID tokens; its access tokens remain
bearer tokens. This enrollment flow uses the standard signed nonce instead.
