# Changelog

All notable changes to `Novalist.Sync.Contracts` are documented here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
the package uses [Semantic Versioning](https://semver.org/): breaking wire
changes bump `Protocol.Version` and the major version, additive changes bump
the minor version.

## [Unreleased]

### Added

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
