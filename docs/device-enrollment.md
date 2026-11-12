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

The earlier `cnf.jkt` enrollment path remains available for callers that already
obtain a provider-signed, key-bound MFA access token. It is not the browser flow
and does not implement the full DPoP HTTP protocol.

Protocol references: [OIDC nonce, auth_time and max_age](https://openid.net/specs/openid-connect-core-1_0.html)
and [authentik key binding](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/key-binding/).
authentik's documented key binding applies to ID tokens; its access tokens remain
bearer tokens. This enrollment flow uses the standard signed nonce instead.
