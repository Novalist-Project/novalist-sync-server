# Changelog

All notable changes to `Novalist.Sync.Contracts` are documented here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
the package uses [Semantic Versioning](https://semver.org/): breaking wire
changes bump `Protocol.Version` and the major version, additive changes bump
the minor version.

## [Unreleased]

### Added

- Contracts 1.13.0: Self-service lost-device identity resets. `NativeClientConfiguration.AccountIdentityReset` advertises `/account/reset/challenge` and `/account/reset` (`Protocol.AccountResetChallengePath`, `Protocol.AccountResetPath`). A reset-specific, single-use browser-MFA challenge plus `AccountIdentityResetRequest.ForfeitProjectAccess` replaces the authenticated user's internal identity; `AccountIdentityResetResult.UserId` identifies its fresh account. Historical identities and keys are retained, old devices are revoked, and no project access transfers. Enrollment proofs cannot authorize resets or vice versa. Additive HTTP contracts; the envelope protocol remains 1.

- Contracts 1.12.0: Key-bound first-device enrollment in one browser sign-in. `DeviceProof.EnrollmentNonce` derives the OIDC nonce from the canonical server URL, both public-key records and a 32-byte client salt (`DeviceProof.EnrollmentSaltLength`), so a client can sign in before it knows its account ID and still bind that sign-in to its keys. `DeviceEnrollmentProof.Salt` carries the salt; a key-bound proof leaves `ChallengeId` empty. `NativeClientConfiguration.KeyBoundEnrollment` tells clients that the server accepts these proofs. The server-issued challenge flow is unchanged. The new member, property and helper are additive; the envelope protocol remains 1.

- Contracts 1.11.0: History compaction without a key change. Any writing member reserves the project with `ReserveProjectCompaction`, stages a fresh encrypted checkpoint at the current epoch with `StageProjectCompaction`, and `CommitProjectCompaction` moves the history boundary to the first checkpoint update. Keys, grants, invitations and field leases are unchanged; members receive `ProjectHistoryCompacted`. `CancelProjectCompaction` releases the reservation. `GetProjectHistory` returns `ProjectHistoryStatus` with the boundary, the stored bytes since it and the newest checkpoint size, so clients can decide when to compact. `Protocol.TransitionInProgress` and `Protocol.HistoryReplaced` are the stable texts of the push and catch-up refusals: clients retry the first and load the newest checkpoint on the second. New methods, the record and the client callback are additive; the envelope protocol remains 1.

- Contracts 1.10.0: Owner authorization of members' unregistered devices. `ListPendingProjectDevices` returns members' active, unauthorized devices with their linking chains (`PendingProjectDevice`, `ProjectDeviceLink`, capped at `ProjectDeviceAuthorizationLimits.MaximumLinkChain`). `AuthorizeProjectDevice` stores an owner-signed certificate and an opaque current-epoch key grant atomically; it is rejected while a key rotation is reserved. The device receives `ProjectDeviceAuthorized` and reads its grant with `GetLatestProjectEpochGrant`, which also returns epoch 0 grants. New methods, records and the client callback are additive; the envelope protocol remains 1.

- Contracts 1.9.0: Blob garbage collection. Writers that have caught up to the whole log stream every blob ID their current manifest references to `ReportBlobReferences`, together with that `caughtUpToServerSeq`; reports behind or beyond the log are rejected. `BlobTransfer.MaximumReportedReferences` bounds one report. A report marks each blob it omits as unreferenced. The blob becomes collectable only when a later report, sent at least the server's grace period after the first, still omits it. Uploading a new chunk to a blob clears its marks. The method and constant are additive; the envelope protocol remains 1.

- Contracts 1.8.0: Immutable encrypted blob chunk uploads and independently authorized chunk downloads over the authenticated hub. `BlobChunk` and `BlobTransfer` define zero-based chunk indices and a 64 KiB plaintext chunk limit with a 16-byte authentication tag. Clients resume transfers by requesting missing indices. New methods and records are additive; the envelope protocol remains 1.

- Contracts 1.7.0: Field leases: acquire, renew, release and guarded encrypted edits. Grants include server time and catch-up boundaries; fixed acquisition deadlines prevent delayed retries from reviving expired focus. New methods and records are additive.

- Contracts 1.6.0: Epoch rotation: verified project-device certificates, staged checkpoints and opaque key grants, reservations, cancellation, compare-and-set commit, and `ProjectEpochChanged`. `CommitProjectEpoch.RemovedMemberId` may be omitted or null to rotate without removing a member, including after device revocation. New methods and records are additive.

- Contracts 1.5.0: Presence: transient encrypted `PublishPresence` and `ReceivePresence` messages. Delivery rechecks account, device and membership without persisting payloads or holding database locks across network sends.

- Contracts 1.4.0: Membership management: owner-only member listing, removal and project deletion, stable role names, and `ProjectAccessEnded` notifications. New methods and records are additive.

- Contracts 1.3.0: Invitations: owner-issued single-use claims, revocation tombstones and device/certificate-bound activation receipts, with authenticated hub and legacy HTTP entry points. Invitation records and hub methods are additive.

- Contracts 1.2.0: Verified devices and recovery: public-key possession proofs, linked recovery identities, and a one-use browser-MFA enrollment challenge bound to ordinary and recovery keys. `DeviceEnrollment*`, `DeviceProof`, registration paths and device authentication hub methods are additive.

- Contracts 1.1.0: Relay and native client configuration: opt-in relay routing, optional authentication, limits, connection renewal and bounded authorization grace. `NativeClientConfiguration`, `RelayProtocol` and client configuration path constants are additive; the envelope protocol remains 1.

- `Protocol.Version` (protocol version 1).
- `MsgType` enum: `Update = 1`, `Snapshot = 2`, `Presence = 3`.
- `EnvelopeHeader` record with the 62-byte layout from design.md §7:
  `WriteTo`, `ToBytes` and `Parse` (big-endian integers, RFC 9562 UUID byte
  order).
- `UnsupportedProtocolVersionException`, thrown by `EnvelopeHeader.Parse` for
  an unknown `protoVersion`.
- `Envelope` record: raw header bytes, nonce, ciphertext and signature, all
  opaque `byte[]`.
- `SequencedEnvelope` record: an `Envelope` with the `ServerSeq` the server
  assigned.
- `Protocol.HubPath` (`/sync`), the path of the SignalR hub.
- `Protocol.DevicesPath` (`/devices`): devices register with
  `PUT /devices/{deviceId}` before connecting to the hub.
- `Protocol.DeviceIdParameter` (`deviceId`): the query parameter that names
  the connecting device, as in `/sync?deviceId=<guid>`.
- `ISyncHub` (client → server): `CreateProject` (the caller becomes owner),
  `JoinProject` / `LeaveProject` (start / stop receiving live updates of a
  project), `Push` (returns the assigned `serverSeq`) and `CatchUp` (streams
  every update after `afterServerSeq`).
- `ISyncClient` (server → client): `ReceiveUpdate`.
