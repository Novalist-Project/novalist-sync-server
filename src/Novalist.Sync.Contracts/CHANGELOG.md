# Changelog

All notable changes to `Novalist.Sync.Contracts` are documented here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
the package uses [Semantic Versioning](https://semver.org/): breaking wire
changes bump `Protocol.Version` and the major version, additive changes bump
the minor version.

## [Unreleased]

### Added

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
