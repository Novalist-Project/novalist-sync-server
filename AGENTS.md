# AGENTS.md — novalist-sync-server

Instructions for AI coding agents (and humans) working in this repository.

## What this repo is

The server half of **Novalist Sync**: end-to-end encrypted, real-time
collaboration for [Novalist](https://github.com/Drommedhar/novalist-official)
projects.

The server is a **blind relay and store**. It authenticates users, checks
project membership, orders and stores encrypted messages, serves catch-up and
snapshots, stores encrypted blob chunks, and broadcasts to connected clients.
**It never sees plaintext or keys.** All merging (CRDT, done with Yjs in the
desktop app), encryption and validation happen in the client, which lives in
the Novalist fork, not here.

The full design is in `docs/design.md`. Read it before making non-trivial
changes. Section 11 (decision log) records choices that are already settled; do
not re-open them in code without being asked.

## Repository layout

```
src/
  Novalist.Sync.Server/       ASP.NET Core app: SignalR hub, auth, persistence, blob endpoints, retention jobs
  Novalist.Sync.Contracts/    Wire records + hub interface + protocol version. Published as a NuGet package.
tests/
  Novalist.Sync.Server.Tests/
  Novalist.Sync.Contracts.Tests/
deploy/                      Production Compose examples for relay and persistent hosting
docs/
  design.md
  transport.md
```

If the actual layout differs from this, follow the actual layout and update this
file.

## Development environment

The dev environment is managed with **[devenv](https://devenv.sh)**
(`devenv.nix` at the repo root). It provides the .NET 10 SDK, `dotnet-ef` and
the local PostgreSQL service. Do not install SDKs or databases globally, and do
not add Docker Compose files for development or test services; if a tool or
service is missing, add it to `devenv.nix`.

Production self-hosting examples belong in `deploy/`; see `deploy/README.md`
when changing their Compose files, proxy configuration or OIDC setup. Validate
deployment changes with Docker without replacing the devenv development services.

Run all commands inside the devenv shell (`devenv shell`, or automatically via
direnv if configured).

## Commands

```bash
devenv shell                                   # enter the dev environment
devenv up                                      # start services (PostgreSQL) in the foreground

dotnet build                                   # build everything
dotnet test                                    # run all tests (needs `devenv up` running)
dotnet run --project src/Novalist.Sync.Server  # run the server locally
dotnet format                                  # format before committing

# database migrations (EF Core)
dotnet ef migrations add <Name> --project src/Novalist.Sync.Server
dotnet ef database update --project src/Novalist.Sync.Server

# contracts package
dotnet pack src/Novalist.Sync.Contracts -c Release
contracts-pack                                 # pack a fresh local prerelease into ../.nuget-feed
```

For cross-repository development with the Novalist fork, see
[`docs/local-packages.md`](docs/local-packages.md). The local feed is not used
by CI or published releases.

The connection string for local development and tests comes from environment
variables set in `devenv.nix`. Never hard-code credentials in
`appsettings*.json`.

Before declaring a task done: `dotnet build` has no warnings you introduced, and
`dotnet test` passes.

## Security rules (non-negotiable)

These exist because the whole product is "the server can't read your novel".
Breaking one silently breaks the product.

1. **Never add decryption, key handling or CRDT logic to the server.** No CRDT
   library (Yjs, YDotNet or similar) in `Novalist.Sync.Server`. No code that
   attempts to interpret `ciphertext`, `wrapped`, blob chunk or snapshot payload
   bytes. Treat them as opaque `byte[]`.
2. **The server may only read envelope headers** (`protoVersion`, `msgType`,
   `projectId`, `itemId`, `epoch`, `senderDeviceId`, `senderSeq`) and the public
   metadata listed in design.md §10. Do not add new plaintext fields to wire
   messages without updating design.md and flagging it in the PR description —
   every new field is new metadata the server learns.
3. **Never log payloads.** Logs may contain IDs, sizes, sequence numbers and
   timings. Never ciphertext, nonces, signatures, wrapped keys, or anything a
   client sends inside an encrypted payload. Do not log request/response bodies
   of hub calls or blob endpoints.
4. **Membership is checked on every operation** — joining a project group,
   pushing, catch-up, snapshot upload/download, blob upload/download, key fetch.
   A device removed or revoked must lose access immediately, including active
   SignalR connections.
5. **Epoch changes are compare-and-set.** Accepting epoch `e+1` must be atomic
   (single transaction / conditional update); a second concurrent rotation to
   `e+1` is rejected, never merged.
6. **`serverSeq` is assigned by the database**, strictly increasing per project,
   with no gaps under concurrency. Never assign it in application memory.
7. **No custom crypto on the server.** The only cryptography the server may do
   (if design.md says so) is _verifying_ Ed25519 signatures against stored
   public keys, via NSec. Never generate, store or handle private keys or
   symmetric keys.
8. Never weaken auth for convenience in tests or dev by changing production code
   paths. Use test-only configuration.

If a task seems to require breaking one of these rules, stop and ask instead.

## Contracts package rules

`Novalist.Sync.Contracts` is consumed by the Novalist fork as a NuGet package,
so changes here are API changes.

- Contracts contain **only** records, enums, constants, exceptions, the hub
  interface and pure wire encoding/decoding (e.g. `EnvelopeHeader.WriteTo` /
  `Parse`). No other logic, no dependencies beyond the BCL. Encoding lives here
  so client and server share one implementation of the bytes that are encrypted
  and signed over.
- Byte layouts (the envelope header in design.md §7) must match the spec
  exactly. Add a test for any layout change.
- **Breaking wire changes bump `Protocol.Version`** and the package major
  version. Additive changes bump minor.
- The server must reject messages with an unsupported `protoVersion` with a
  clear error, not misparse them.
- Update `src/Novalist.Sync.Contracts/CHANGELOG.md` for every Contracts change.

## Code conventions

- **.NET 10** (`net10.0`) for all projects, including Contracts. Novalist is
  moving to .NET 10; do not target older frameworks. C# latest, nullable
  reference types enabled, warnings as errors in CI.
- `async` all the way; pass `CancellationToken` through hub methods, endpoints
  and repository calls.
- File-scoped namespaces, records for immutable data, primary constructors where
  they keep things short.
- Data access through EF Core with `Npgsql`. Large binary columns (`ciphertext`,
  blob chunks) are `bytea`; never load blob chunks into memory all at once —
  stream them.
- Keep hub methods thin: validate → authorize → call a service → return.
  Business rules live in services that are unit-testable without SignalR.
- No new NuGet dependencies without a reason stated in the PR description.

## Testing

- Unit tests for services (membership checks, epoch compare-and-set, retention
  selection, GC).
- Integration tests with a real PostgreSQL (the devenv service, using a fresh
  database per test run) and `WebApplicationFactory` for hub and endpoint
  behavior. No Testcontainers/Docker.
- Tests use random bytes as "ciphertext". The server must behave identically for
  any payload content — that is the point.
- Required test cases when touching these areas:
  - concurrent pushes produce gap-free, strictly increasing `serverSeq`
  - removed member / revoked device is rejected on every operation, including an
    already-open connection
  - two concurrent rotations to the same epoch: exactly one wins
  - catch-up after compaction returns the right snapshot followed by the
    remaining updates
  - unsupported `protoVersion` is rejected
  - nothing in log output contains payload bytes (assert on captured logs)

## Scope boundaries

- Client-side work (CRDT, FileBridge, editor binding, key management, recovery)
  belongs in the Novalist fork, not here.
- Do not modify `docs/design.md` decisions (§11) unless explicitly asked. You
  may fix typos or update sections to match implemented reality, and must
  mention it.
- Keep commits focused: one concern per commit, imperative subject line.
