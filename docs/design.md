# Novalist Sync — Design

Status: draft v0.3 · Last updated: 2026-10-19

## 1. Goal

Add end-to-end encrypted (E2EE), real-time collaborative editing to **Novalist**
(github.com/Drommedhar/novalist-official). Several users can work on the same
**project** at the same time, work offline, and share everything a project
contains: scenes, Codex entries, structure, images and research files. The sync
server stores and relays data but **can never read project content, file
content, or keys**.

Novalist stays a **local-first** writing application. Sync is **optional per
project** and off by default: a project that never enables sync works exactly as
before, with no account, server, network connection or sync configuration
(§5.4).

Priorities, in order: **data integrity, backward compatibility, editing
correctness, offline reliability, real-time synchronization.** When two goals
conflict, the earlier one wins.

### Non-goals (v1)

- **Hiding metadata** from the server (who works on which project, when, how
  much, how many items).
- **Full forward secrecy.** For a persistent shared document it largely doesn't
  apply: past edits _are_ the current document, and any device that can show the
  project holds its plaintext. Catch-up, new devices and version history all
  require old keys to stay available. What we do instead: key rotation on member
  removal, device revocation and monthly (§6.2), plus compaction so little old
  ciphertext stays on the server (§8.4). Real FS/PCS for key distribution via
  MLS is future work (§13).
- **Mobile** (`Novalist.Mobile`) — desktop first.

## 2. Novalist as it is today

Facts the design has to fit (as of v3.6, with the fork on .NET 10):

- **Electron + React** front end (`app/`) over a bundled **.NET core process**
  (`Novalist.Backend`), talking **JSON-RPC over stdio**. The Electron main
  process starts the backend and routes messages to it. All project logic lives
  in .NET (`Novalist.Core`).
- **Scene editor:** custom WYSIWYG in an iframe (`contentEditable` + DOMPurify
  sanitizer, `app/src/renderer/public/editor/`). Typing is largely left to the
  browser and `document.execCommand`; changes reach the host as **complete
  scene HTML** (`content-input.js`), and loading a scene replaces the DOM
  (`editor-content.js`). The editor DOM also carries **presentation**: grammar
  highlights, pagination wrappers, display URLs for images. Codex sections use
  CodeMirror (Markdown). No Yjs or collaboration code exists.
- **Storage:** a project is a plain folder. Scenes are **HTML** files, entities
  and metadata are **JSON**, images and research files sit alongside. Scene
  identity is a one-line comment in each file; chapter identity is a hidden
  folder marker. Per-project settings live in `.novalist/`.
- **Filesystem is the source of truth:** Novalist reconciles outside changes on
  open and live while running.
- Built-in **Git** client, automatic **ZIP backups**, per-scene **snapshots**.
- **Extensions** are .NET 8 libraries loaded by the core, with UI only in
  sandboxed webviews. They cannot reach into the editor, and the Mac App Store
  build loads none. Hence sync is integrated into Novalist itself, not shipped
  as an extension.
- MIT licensed. The maintainer accepts PRs but promises no maintenance.

## 3. Threat model

| Actor                      | Assumed capability                                                                     | Must not be able to                                           |
| -------------------------- | -------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| Sync server / its operator | Reads and modifies everything it stores and relays; can drop, reorder, replay messages | Read content, forge edits, silently insert itself as a member |
| Network attacker           | Full control of the network                                                            | Anything beyond what the server can (TLS on top anyway)       |
| Removed member             | Kept all keys they ever had                                                            | Read edits made **after** removal                             |
| Current member (malicious) | Holds the project key                                                                  | Impersonate another member's edits                            |
| Novalist extension         | Runs in the core process (code) or a webview (UI)                                      | — see below                                                   |

Accepted limitations:

- A current member can read everything and can write garbage. E2EE protects
  against outsiders, not against collaborators you invited.
- Without key verification (§8.6), a malicious server can substitute public keys
  when a member is first added.
- The server can withhold updates. Clients detect gaps in per-device sequence
  numbers but cannot force delivery.
- **Extensions run in the same .NET process as the sync engine**, so a malicious
  installed extension could read keys. Keys are never sent to the renderer or
  the Yjs host, which at least keeps them away from extension webviews and the
  web content in general. Users must trust the extensions they install, as they
  already must today.
- **The editor and the Yjs host hold plaintext.** They have to: they show and
  merge the document. They never receive keys, ciphertext or signatures (§4.3).

## 4. Architecture

```
┌──────────────── Novalist (fork) ──────────────────────────────────┐
│ Electron renderer (React)                                          │
│   Scene editor iframe (existing contentEditable editor)            │
│     └─ NovalistYjsBinding: local edits → Yjs, Yjs → DOM patches    │
│        │  MessagePort (plaintext Yjs updates, no keys)             │
│ Yjs host (Electron utilityProcess)                                 │
│   └─ YjsDocumentManager                                            │
│        ├─ Y.Doc per project item (Y.XmlFragment / Y.Map / Y.Text)  │
│        ├─ Projection: Y.Doc → HTML / JSON                          │
│        └─ Import: HTML / JSON → Y.Doc (open, outside edits)        │
│        │  MessagePort → main → JSON-RPC over stdio (no keys)       │
│ Novalist.Backend (.NET)                                            │
│   ├─ Novalist.Core (existing project logic, files, file watcher)   │
│   └─ Novalist.Sync.Client (only for synced projects)               │
│        ├─ SQLite: durable Yjs log, outgoing queue, sync state,     │
│        │          keys (app data, never in the project folder)     │
│        ├─ NSec (all crypto: seal, verify, wrap)                    │
│        └─ SignalR client                                           │
└──────────────────────────┬─────────────────────────────────────────┘
                           │ TLS WebSocket, ciphertext only
┌──────────────────────────▼─────────────────────────────────────────┐
│ novalist-sync-server (ASP.NET Core)                                │
│   ├─ SignalR hub (relay, catch-up, presence)                       │
│   ├─ Auth + membership                                             │
│   ├─ PostgreSQL (log, snapshots, wrapped keys, epochs)             │
│   └─ Blob store (encrypted chunks)                                 │
└────────────────────────────────────────────────────────────────────┘
```

Rules:

- **If code needs a key, it belongs in Novalist.Sync.Client (.NET).**
- **If code merges or interprets document content, it belongs in the Yjs host
  or the editor binding (JavaScript, Yjs).** There is no CRDT code in .NET and
  no YDotNet anywhere.
- **The server does neither.** It never decrypts, never merges, never
  interprets a payload.

Responsibilities:

| Component              | Owns                                                                                      |
| ---------------------- | ----------------------------------------------------------------------------------------- |
| Existing editor        | User interaction, formatting, rendering, selection, editing UX                            |
| `NovalistYjsBinding`   | Turning local edits into Yjs transactions and remote changes into precise DOM patches     |
| `YjsDocumentManager`   | Live document state, merging, relative positions, collaborative undo, HTML/JSON projection |
| `Novalist.Sync.Client` | Encryption, signatures, keys, durable local state, outgoing queue, server connection      |
| Sync server            | Authorization, storage, sequencing, relaying opaque encrypted updates                     |

### 4.1 Projects and repositories

| Repo                 | Project                                              | Contents                                                                                              |
| -------------------- | ---------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| Novalist fork        | `Novalist.Sync.Client` (new)                         | Crypto, keys, durable Yjs log, outgoing queue, connection, sealing snapshots. No CRDT code.           |
| Novalist fork        | `Novalist.Backend` / `Novalist.Core` (small changes) | Host Sync.Client, new `sync.*` JSON-RPC methods, write projections, hook into file watcher            |
| Novalist fork        | `app/` (changes)                                     | Yjs host, `YjsDocumentManager`, editor binding, remote cursors, sync and sharing screens              |
| novalist-sync-server | `Novalist.Sync.Server`                               | Hub, auth, persistence, blob endpoints, snapshot retention                                            |
| novalist-sync-server | `Novalist.Sync.Contracts`                            | Wire records, hub interface, protocol version. Published as a **NuGet package** consumed by the fork. |

Changes to Novalist itself are kept as small and isolated as possible so they
can be offered upstream as a PR. Talk to the maintainer before the PR gets
large.

### 4.2 Licensing

| Project                   | License           |
| ------------------------- | ----------------- |
| `Novalist.Sync.Client`    | MIT               |
| `Novalist.Sync.Contracts` | MIT               |
| `Novalist.Sync.Server`    | AGPL-3.0-or-later |

Sync.Client lives in the Novalist fork and shares Novalist's MIT license.
Contracts is MIT so the fork and third-party clients can depend on it without
taking on copyleft. The server is AGPL so anyone hosting a modified server for
others must publish their changes (AGPL §13).

### 4.3 Processes and IPC

- The **Yjs host** is an Electron `utilityProcess`. It owns every open
  project's `Y.Doc`s, independent of any window or editor iframe: closing a tab
  or switching scenes never discards an edit, and updates for scenes that are
  not open are still merged and projected to disk.
- The **editor iframe** talks to the Yjs host over a `MessagePort` handed out by
  the main process (`MessageChannelMain`), so keystrokes do not detour through
  the main process.
- The **Yjs host** talks to `.NET` through the main process and the existing
  JSON-RPC channel. Yjs updates are binary and travel base64-encoded.
- **Keys, ciphertext, nonces and signatures never leave .NET.** The Yjs host
  and the renderer only ever see plaintext Yjs updates and documents.

## 5. Data model

### 5.1 The project is the sync unit

One shared project = one **sync space** with one member list and one key
hierarchy. Inside it, every item is its own CRDT document (a Yjs `Y.Doc`),
identified by a random 128-bit `itemId`:

| Item kind                                                                  | CRDT shape                                                                                                                                                                                           | On disk                                 |
| -------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------- |
| **Structure** (one per project)                                            | Y.Map/Y.Array: books, drafts, acts, chapters, scene order, metadata                                                                                                                                  | chapter folders + markers, project JSON |
| **Scene**                                                                  | Y.XmlFragment mirroring the editor's sanitized block structure (paragraph styles, lists, footnotes, comments; inline formatting as attributes) + Y.Map for synopsis, notes, POV, status, dates, tags | scene `.html`                           |
| **Codex entry / other JSON** (entities, plotlines, timeline, boards, maps) | Y.Map per file; long-form Markdown sections as Y.Text                                                                                                                                                | `.json` / `.md`                         |
| **File manifest** (one per project)                                        | Y.Map: relative path → `BlobRef` (§9.2)                                                                                                                                                              | images, research, any other file        |

Scenes are only loaded into the Yjs host when opened or when updates for them
arrive.

The `itemId ↔ Novalist identity` mapping (scene ID comment, chapter marker, file
path) lives inside the encrypted structure doc, so the server never sees names
or paths.

### 5.2 Projection and file bridge: Yjs ⇄ filesystem

The existing HTML/JSON files stay the portable local representation of every
project, synced or not, and can still be edited outside Novalist. For a
**synced** project the `Y.Doc` is the authoritative live state and the files are
its projection; for a **local-only** project the files stay authoritative
(§5.4).

The work is split along the process boundary:

- **Yjs host** projects a `Y.Doc` to HTML/JSON, and imports HTML/JSON into a
  `Y.Doc` or diffs it against one. Only the side that holds the CRDT can do
  either.
- **.NET** writes and reads the files through Novalist's existing services, so
  files look exactly as they do today, and owns the file watcher.

Directions:

- **Yjs → disk:** after local or remote changes, the Yjs host projects the
  affected item (debounced, ~500 ms) and .NET writes it. .NET records the hash
  of what it wrote.
- **Disk → Yjs:** when the file watcher reports a change whose hash is _not_ one
  it just wrote (an outside edit, a `git pull`, a cloud folder sync), .NET hands
  the new content to the Yjs host, which diffs it against the current projection
  and applies the difference as Yjs operations: a text diff per block for
  scenes and Markdown, a field diff for JSON. Deleted/added files become
  structure or manifest changes.
- **When a diff cannot be applied reliably** (for example a block structure the
  importer does not recognise, or a file that changed beyond recognition), the
  file is **not** merged. Both versions are kept (the outside version as a
  conflict copy) and the user resolves it explicitly. Collaborative state is
  never silently overwritten.
- **Echo suppression** by content hash prevents write → watcher → re-apply
  loops.
- **Snapshots, backups and restore** in a synced project are applied as an
  outside edit (a diff into the existing `Y.Doc`), never by replacing the
  `Y.Doc`. That keeps item identities and the CRDT history, and does not wipe
  collaborators' concurrent changes.
- A synced document is **never rebuilt from HTML alone**. That would lose its
  CRDT history and identities; the durable Yjs state (§5.5) is what it is
  loaded from.

### 5.3 Editor binding

The existing editor stays. There is no move to ProseMirror, Tiptap or another
editor framework. A custom **`NovalistYjsBinding`** in the editor iframe connects
it to the scene's `Y.XmlFragment` in the Yjs host.

**Schema first.** The binding needs an explicit model of what a scene is:
block types and paragraph styles, lists, inline marks (bold, italic, underline
…), comments, footnotes, suggestions, mentions, images. That schema defines the
`Y.XmlFragment` shape and is the contract between importer, projection and
binding (§12).

- **Local edits.** `beforeinput` / `input` and the editor's own operations
  (formatting, paste, dictation, suggestions) only tell the binding **which
  blocks changed**. It then reads those blocks' **content in schema form**, the
  same cleaned form the editor already produces for saving, and diffs it against
  the `Y.XmlFragment` into fine-grained Yjs operations in one transaction with
  the binding as origin. A `MutationObserver` is never the source of CRDT
  operations: the editor rewrites its DOM for presentation too.
- **Presentation is not content.** Grammar highlights, pagination wrappers,
  display URLs, selection and focus styling are a separate layer that the
  schema form leaves out, so they can never become collaborative edits.
- **IME and dictation.** No operations are generated during composition; the
  affected block is reconciled once composition ends.
- **Remote edits.** Yjs change events become **precise DOM patches** of the
  affected blocks. The editor's HTML is never replaced wholesale while the user
  is editing, and the local selection is kept through the patch.
- **Undo** uses `Y.UndoManager`, tracking only the binding's own origin, so undo
  never removes a collaborator's edits.
- **Cursors** are exchanged as Yjs _relative positions_, so the local caret and
  remote cursors stay attached to the right character while text around them
  changes.

The same binding runs for local-only and synced projects; only persistence
differs (§5.4). It does not ship until it has been validated under concurrent
edits (§14); until then the current editor path stays the production default.

This binding is the largest and riskiest piece of new work, because Novalist's
editor has no existing document model or CRDT integration.

### 5.4 Sync modes

| Mode                       | Behavior                                                                 | Authoritative state        |
| -------------------------- | ------------------------------------------------------------------------ | -------------------------- |
| **Local-only** (default)   | No server, account, keys or network. Works exactly as before.             | HTML/JSON files            |
| **Synced, online**         | Real-time E2EE collaboration through SignalR                             | Durable Yjs state (§5.5)   |
| **Synced, offline/paused** | Editing continues locally; queued updates go out when connected again    | Durable Yjs state (§5.5)   |

- **Local-only:** the Yjs host may build an in-memory `Y.Doc` from the files when
  a scene is opened, and saves go to the files through the existing save path
  and its conflict protections. Nothing is stored in the sync database.
- **Synced:** every edit is durably stored and queued before it counts as saved
  (§5.5). A synced project that is offline, paused or whose server is unreachable
  **stays synced**; it never silently becomes local-only.

User actions, deliberately separate (no single on/off toggle that could discard
unsynchronized changes):

- **Enable sync** turns a local-only project into a synced one (§8.1).
- **Pause sync** stops network traffic. Project association, keys and the
  outgoing queue are kept, and editing continues.
- **Resume sync** reconnects, sends the queue and catches up.
- **Make local copy** creates a separate local-only project from the current
  state. It gets new identities and no sync association, so nothing done to the
  copy reaches the shared original.

### 5.5 Durability

For synced projects, `Novalist.Sync.Client` keeps a SQLite database in the
app-data directory (never in the project folder, like the keys, §6.2):

- **Local update log:** every Yjs update of every item, local or remote, plus
  periodic compacted states (`Y.encodeStateAsUpdate` from the Yjs host), so an
  item can be reloaded without the server and without the HTML.
- **Outgoing queue:** local updates not yet acknowledged by the server, with
  their `senderSeq`.
- **Sync state:** `lastServerSeq` per project, epoch, connection settings.

A local edit is **safely saved** once .NET has written it to the local update
log **and** the outgoing queue **in one transaction** and acknowledged that to
the Yjs host. Network delivery is tracked separately: an update leaves the queue
only when the server has assigned it a `serverSeq`. Incoming updates are written
to the local log before `lastServerSeq` advances. After a crash or restart, the
Yjs host reloads every item from this log and re-projects it, and the queue is
sent as soon as sync is online.

The local log holds plaintext Yjs updates, at the same protection level as the
project files themselves.

## 6. Cryptography

### 6.1 Primitives

| Purpose                     | Algorithm                                 | Library            |
| --------------------------- | ----------------------------------------- | ------------------ |
| Symmetric encryption (AEAD) | XChaCha20-Poly1305 (24-byte random nonce) | NSec               |
| Signatures                  | Ed25519                                   | NSec               |
| Key agreement               | X25519                                    | NSec               |
| Key derivation              | HKDF-SHA256                               | NSec / .NET `HKDF` |
| Hashing                     | SHA-256                                   | .NET               |
| Randomness                  | `RandomNumberGenerator`                   | .NET               |

XChaCha20-Poly1305's 192-bit nonce is safe to generate randomly, so nonce reuse
is not a concern even with many devices writing under the same key. All
derivations use versioned `info` strings (`"novalist/v1/..."`) for domain
separation.

### 6.2 Keys

**Device identity keys.** Each Novalist installation generates an Ed25519 key
pair (`DSK`, signing) and an X25519 key pair (`DXK`, receiving keys). Public
halves are uploaded; private halves stay on the device in a SQLite database **in
the app-data directory, never inside the project folder** (so Git, backups and
folder sync never pick them up), encrypted at rest with DPAPI / macOS Keychain /
libsecret.

New devices of the same user are **linked** by an existing device (QR or pairing
code), which signs the new device's public keys.

**Project epoch keys.** Each project has a sequence of epochs, each with a
random 32-byte key `EK_e`. Subkeys per purpose:

```
K_update   = HKDF(EK_e, salt = projectId, info = "novalist/v1/update")
K_snapshot = HKDF(EK_e, salt = projectId, info = "novalist/v1/snapshot")
K_presence = HKDF(EK_e, salt = projectId, info = "novalist/v1/presence")
```

**A new epoch starts when:**

- a member is removed,
- a device is revoked (lost/stolen laptop),
- **30 days** have passed since the current epoch started.

Rotation is a compare-and-set on the server: the first client to submit epoch
`e+1` wins; a client whose rotation is rejected adopts the winner's epoch. Old
epoch keys are kept, so history stays readable.

**Blob keys.** Each blob has its own random 32-byte key, stored inside the
encrypted file manifest (§9.2).

### 6.3 Key wrapping

To give `EK_e` to recipient device `R`, a granting member device `G`:

```
eph       = new X25519 key pair (single use)
shared    = X25519(eph.private, R.DXK.public)
wrapKey   = HKDF(shared, salt = eph.public || R.DXK.public,
                 info = "novalist/v1/wrap" || projectId || epoch)
nonce     = random(24)
aad       = projectId || epoch || R.deviceId || G.deviceId
wrapped   = XChaCha20Poly1305.Encrypt(wrapKey, nonce, EK_e, aad)
signature = Ed25519.Sign(G.DSK, "novalist/v1/wrap-sig" || aad || eph.public || nonce || wrapped)
```

**The signature is essential.** Without it the server could wrap a key _it_
knows, the recipient would encrypt edits under it, and the server could read
them. The recipient verifies the signature and accepts the key only if `G` is a
device it already trusts as a member of this project.

### 6.4 Recovery key

At account setup the user is shown a **recovery key** once (256 random bits,
displayed as a word list or grouped base32). From it the client derives a
**recovery device**:

```
recSeed       = HKDF-SHA256(recoveryKey, empty salt, "novalist/v1/recovery", 32)
signingSeed   = HKDF-SHA256(recSeed, empty salt, "novalist/v1/recovery/signing", 32)
receivingSeed = HKDF-SHA256(recSeed, empty salt, "novalist/v1/recovery/receiving", 32)
recovery device = Ed25519(signingSeed) + X25519(receivingSeed)
```

First verified enrollment atomically registers the ordinary device and its
recovery device, with independent signing and receiving keys. The recovery
device signs its own registration; the ordinary device signs its linking proof.
An existing verified device can also authorize a recovery device through ordinary
device linking. The client keeps all private key material and clears temporary
derived seeds. The server stores both ordinary and recovery public device records,
never their private keys or the recovery key.

On a new installation, the client re-derives the recovery identity from the saved
key and uses it to sign a replacement device's linking proof. This remains valid
after the ordinary device has been revoked. Revoked records never reopen
token-only enrollment, and a revoked recovery device cannot authorize a link.

This recovers account/device enrollment. Access to previous project content still
requires retained project keys or an authorized collaborator's key grant and
device certificate; enrollment does not recreate missing encryption keys. Full
project recovery needs epoch keys wrapped for the recovery device. The explicit
client enrollment API requires a caller-supplied recovery identity; it does not
silently generate or save an undisclosed recovery secret. Losing every authorized
device and the recovery key leaves no cryptographic recovery path.
The UI must warn that anyone holding the recovery key can authorize replacement
devices and has full access to project content whose epoch keys are wrapped for
the recovery device.

## 7. Message format

```
header = protoVersion(1) || msgType(1) || projectId(16) || itemId(16)
         || epoch(4) || senderDeviceId(16) || senderSeq(8)
```

The header is 62 bytes. `msgType` ∈ { update = 1, snapshot = 2, presence = 3 };
0 and all other values are invalid. `itemId` is a random GUID, so the server
learns _that_ an item exists and how often it changes, but not what it is.

Encoding rules:

- Integers (`epoch` as uint32, `senderSeq` as uint64) are **big-endian**.
- IDs (`projectId`, `itemId`, `senderDeviceId`) are **random UUIDv4**, written in
  RFC 9562 (big-endian) byte order. Not UUIDv7: its embedded timestamp would
  tell the server when each item, project or device was created.
- A receiver checks `protoVersion` **before** anything else, including the
  length, and rejects unsupported versions instead of parsing them with the
  current layout.

`payload` is a binary **Yjs update** for `update` messages, an encoded Yjs state
(`Y.encodeStateAsUpdate`) for `snapshot` messages, and an encoded cursor and
selection state for `presence`. The server cannot tell them apart beyond
`msgType`.

```
nonce      = random(24)
ciphertext = XChaCha20Poly1305.Encrypt(K_type, nonce, payload, aad = header)
signature  = Ed25519.Sign(DSK, "novalist/v1/msg-sig" || header || nonce || ciphertext)
envelope   = { header, nonce, ciphertext, signature }
```

## 8. Protocol flows

### 8.1 Enabling sync for an existing local project

1. The client generates `projectId`, `EK_0`, and `itemId`s for every scene,
   Codex file and asset.
2. The Yjs host imports every item from the existing files (§5.2). **Before
   anything is uploaded**, it projects each new `Y.Doc` back to HTML/JSON and
   compares the result with the original file. Any difference aborts enabling
   sync and leaves the project untouched, so enabling sync can never change
   content.
3. .NET stores the initial states durably (§5.5), uploads assets as blobs (§9),
   wraps `EK_0` for the user's own devices and recovery device, and uploads the
   wrapped keys and an initial snapshot of every item.

The project files are not rewritten in the process.

### 8.2 Sending an edit

1. The editor binding or the file bridge produces a Yjs update for one item in
   the Yjs host.
2. The Yjs host sends it to .NET, which stores it in the local log and the
   outgoing queue in one transaction (§5.5) and acknowledges: the edit is saved.
3. When sync is online, the client seals it (§7) with `K_update`, increments its
   `senderSeq`, and pushes it. The server checks membership, assigns
   `serverSeq`, stores, broadcasts. Once `Push` returns, the update leaves the
   queue.

Yjs updates merge in any order, so the server's ordering is not needed for
correctness. `serverSeq` is still what makes delivery durable: acknowledgements,
deduplication of retries (`senderSeq`) and catch-up all depend on it.

### 8.3 Receiving an edit

1. Look up the sender device; verify it is a member.
2. Verify the signature; **reject before decrypting** if invalid.
3. Check the epoch key is available.
4. Decrypt; reject on failure.
5. Store the plaintext update in the local log (§5.5), then advance
   `lastServerSeq`.
6. Hand it to the Yjs host, which applies it to the item's `Y.Doc` inside
   try/catch (a malformed update must not crash Novalist), projects the item to
   disk, and patches the editor if the scene is open.

### 8.4 Catch-up, snapshots and retention

- On connect: `CatchUp(projectId, lastServerSeq)`, process as in §8.3.
- A client creates a snapshot of an item **every 500 updates to that item, or
  when the project is closed**: the Yjs host encodes the state
  (`Y.encodeStateAsUpdate`), and .NET seals it with `K_snapshot` and uploads it
  with `coversServerSeq = N`.
- Updates to that item with `serverSeq <= N` that are covered by a retained
  snapshot are deleted.
- **Tiered retention** (enforced by the server, which only needs timestamps):
  keep snapshots **hourly for 24 hours, daily for 30 days, weekly beyond that**.
  These form the shared version history; Novalist's local per-scene snapshots
  continue to work as before.

### 8.5 Adding a member

1. Inviter fetches the invitee's device list (with linking signatures) and
   verifies it.
2. Inviter shows the safety number (§8.6).
3. Inviter wraps **all epoch keys** for each invitee device and recovery device
   — new members see full history by default.
4. Server records membership.

The implemented membership API also supports a single-use invitation link.
The HTTP flow below is for accounts without verified devices. Verified accounts
use `CreateInvitation`, `RedeemInvitation`/`ActivateInvitation`, and
`RevokeInvitation` on a hub connection authenticated with a device possession proof.
A legacy owner POSTs `CreateInvitation` to `/projects/{projectId}/invitations`, with
a client-generated, cryptographically random UUID and an expiry no more than
seven days away. A signed-in recipient POSTs `RedeemInvitation` to
`/invitations/redeem`. The first account to claim it becomes a contributor;
concurrent claims by other accounts fail. The same account can retry while
the invitation is valid and its membership still exists. An owner can POST
the identifier to `/projects/{projectId}/invitations/revoke` to invalidate it.
Revocation does not remove an already admitted member; membership removal
is a separate operation. Revoked identifiers cannot be reused.

Invitation identifiers are bearer capabilities. Send them in authenticated
request bodies, never in HTTP URLs or logs. Clients must keep the link's
encryption material separate: these endpoints grant server access only and
do not implement device verification, epoch-key distribution or rotation.
The §8.5 key-distribution requirements still apply before editing.

### 8.6 Key verification

```
safetyNumber = SHA-256( sort(userA.identityKeys) || sort(userB.identityKeys) )
```

Shown as digits or a QR code for comparison over another channel. Clients
remember verified keys and **warn loudly** when a known user's keys change.

### 8.7 Removing a member / revoking a device

1. Create `EK_{e+1}` (compare-and-set, §6.2).
2. Wrap it only for remaining devices.
3. Upload fresh snapshots of all items under the new epoch.
4. Server drops the membership/device and stops serving updates and blobs to it.

### 8.8 Presence

Cursors, selections and "is in scene X" are encoded by the Yjs host (relative
positions), sealed with `K_presence` by .NET, broadcast, never stored.

`PublishPresence(Envelope)` relays to other connected project members through
`ReceivePresence`. It checks the active device, current membership and epoch,
message type, sender identity and envelope sizes. The ciphertext is limited
to 4096 bytes. Presence never increments `serverSeq` or appears in catch-up.
Clients throttle outgoing updates, reject stale/replayed presence and expire
inactive cursors locally. The server does not interpret the encrypted state.

Malformed, stale-epoch and unauthorized presence is rejected before recipient
counting and delivery coordination. Transient sender sequences use the full
nonzero unsigned 64-bit range because presence is never stored.
Membership, epoch and active devices are rechecked under process-local account
gates (in UUID order), then the project gate shared with access changes.
Delivery holds no database transaction or row locks. Database writes can proceed
while network delivery is pending; hub pushes wait for the bounded delivery
barrier before appending and broadcasting.
Each message snapshots at most 256 joined recipient connections, and takes
account gates only for active candidate recipients and the sender. Presence is rejected
explicitly above that fan-out limit; project editing remains available. Delivery
targets only captured connections that still belong to the project, after
checking membership and active devices again under the gates.
The send has a one-second cancellation deadline passed into SignalR's outbound
write. Access changes wait for an in-flight send to finish, then later sends
exclude removed or revoked recipients. A timeout reports `Presence delivery
timed out.`; clients may send their next fresh presence state. These gates share
the existing single-instance restriction of connection revocation (§13).

### 8.9 Pausing and resuming

- **Pause:** .NET closes the SignalR connection and stops sending. The project
  stays synced; edits keep being stored and queued (§5.5).
- **Resume:** .NET reconnects, joins the project, catches up from
  `lastServerSeq` and sends the queue in `senderSeq` order. Retries are safe:
  the server returns the stored `serverSeq` for a repeated `senderSeq`.

### 8.10 Making a local copy

The Yjs host projects the current state of every item into a new project
folder. The copy gets new identities and no sync association, keys or queue; it
is an ordinary local-only project. The shared original is not touched.

## 9. Blobs (images, research files, other assets)

### 9.1 Encryption

```
BK     = random(32);  blobId = random(16);  chunkSize = 64 KiB
chunk i: nonce = 16 zero bytes || uint64_be(i)        // unique because BK is single-use
         aad   = "novalist/v1/blob" || blobId || uint64_be(i) || isFinal(1)
         c_i   = XChaCha20Poly1305.Encrypt(BK, nonce, chunk_i, aad)
```

`isFinal` prevents truncation; the index prevents reordering. Chunks transfer
independently, so large files stream and resume.

### 9.2 References

The file manifest (§5.1) maps each relative path to:

```json
{
    "blobId": "...",
    "key": "<BK base64>",
    "size": 1234567,
    "chunkSize": 65536,
    "mime": "image/png",
    "sha256": "<plaintext hash>"
}
```

Changing a file creates a new blob and updates the manifest entry. Paths, names,
MIME types and keys are only visible inside the encrypted manifest.

### 9.3 Rules

- No convergent encryption (no content-derived keys): it would reveal when two
  users hold the same file.
- Garbage collection: clients periodically report referenced blob IDs; the
  server deletes unreferenced blobs after a grace period.
- On member removal, existing blobs stay under their old keys (re-encryption:
  v2).

## 10. What the server stores and sees

| Table          | Contents                                                       | Server can read? |
| -------------- | -------------------------------------------------------------- | ---------------- |
| `users`        | OIDC issuer + subject, email, username (§10.1)                 | yes              |
| `devices`      | deviceId, userId, public keys, linking deviceId and signature | public keys only |
| `projects`     | projectId, current epoch, epoch start time                     | yes              |
| `memberships`  | projectId, userId, role (owner, contributor, viewer)           | yes              |
| `invitations` | invitationId, projectId, expiry, claimant account/device, opaque certificate fingerprint | metadata; no keys |
| `field_leases` | projectId, opaque resourceId, holder deviceId, leaseId, epoch, expiry | metadata; no field names or values |
| `field_lease_attempts` | projectId, deviceId, attempted leaseId, acquisition deadline | temporary metadata |
| `project_device_authorizations` | projectId, deviceId, epoch, public certificate | public certificate only |
| `project_epoch_stages` | rotationId, projectId, initiating deviceId, prior epoch/sequence, count, creation time | yes |
| `project_epoch_envelopes` | rotationId, index, senderSeq, opaque checkpoint envelope | envelope header only |
| `project_epoch_device_grants` | rotationId, recipient deviceId, public certificate, opaque staged grant | metadata; no keys |
| `project_epoch_key_grants` | projectId, recipient/grantor deviceIds, epoch, first serverSeq, opaque grant | metadata; no keys |
| `wrapped_keys` | §6.3 records                                                   | no               |
| `updates`      | serverSeq, header, nonce, ciphertext, signature                | header only      |
| `snapshots`    | coversServerSeq, timestamp, envelope                           | header only      |
| `blob_chunks`  | blobId, index, ciphertext                                      | no               |

Visible metadata: project and item counts, member lists, devices, timestamps,
edit frequency per item, sizes, invitation expiry and claimant identities,
opaque field-focus identifiers and timing, and epoch-transition boundaries.

Verified owner connections can list project members using `ListProjectMembers`.
The response contains the existing account UUID, the stable string role
(`Owner`, `Contributor` or `Viewer`) and username (or verified
email when no nonblank username exists, otherwise the account UUID).
`RemoveProjectMember` removes access and the
account's field leases and expires all unused project invitations.
`DeleteProject` removes memberships, invitations, updates and field lease data;
the empty project row remains as an identifier tombstone. Both operations remove
affected connections from live delivery and send `ProjectAccessEnded(projectId)`.
Registered encrypted projects require `CommitProjectEpoch` for member removal.
Members with no registered project device can be removed directly. Epoch
commits lock the initiator and, when supplied, the removed account in database UUID order, then
revalidate the initiating device before changing keys. The delivery barrier
remains held through post-commit connection revocation, which finishes even
when the initiating connection disconnects.
Clients register their public project certificate; contributor registration must
match the certificate fingerprint atomically bound during invitation activation.
The server exposes registered certificate bytes and account/device IDs to the
owner, who validates the device's receiving key against the existing project
authority before wrapping a new random epoch key.

Epoch transitions reserve a stable update boundary before clients catch up and
construct the new checkpoint. `ReserveProjectEpoch` returns that boundary and
fences new old-epoch writes for up to two minutes; confirmed update retries still
return their original sequence. One reservation may be active per project.
`CancelProjectEpoch` releases an abandoned reservation, and periodic cleanup
deletes expired staging data. Clients cancel on failure and retry from a fresh
reservation if the deadline expires. Transitions then stage opaque checkpoint
envelopes and individual opaque grants.
The database atomically compares the previous epoch and last committed sequence,
assigns checkpoint sequences through the normal log allocator, optionally removes
the chosen membership, retires all invitations, clears leases and publishes grants only to
remaining active registered devices. `CommitProjectEpoch.RemovedMemberId` may be
omitted or null to rotate without removing a member, for example after revoking
a stolen device or for periodic rotation. Revoke a stolen device before rotating;
rotation alone does not revoke it, and every active registered device must receive
a grant. Members without a registered, authorized device do not block rotation
and receive no key grant. An API for an owner to authorize those devices and
distribute their keys is not implemented yet.
The commit still checks the reserved epoch and sequence atomically. Devices
receive `ProjectEpochChanged` and restart against the owner-signed checkpoint
boundary. Public metadata adds rotation IDs, device certificates, previous epochs,
checkpoint boundary sequences, envelope/grant sizes and staging timestamps.
The server never decrypts grants or checkpoint envelopes; clients verify the
signature, pinned owner, recipient, monotonic epoch and checkpoint hash.

Field editing adds `field_leases`: project UUID, opaque resource UUID, holder
device UUID, random lease UUID, epoch and expiry. The resource UUID identifies a
client-defined field without disclosing its name, path or value. The server learns
when the same field is focused and by which device. `AcquireFieldLease`,
`RenewFieldLease` and `ReleaseFieldLease` require current write access and device
authentication. A grant lasts 30 seconds; renewal requires the same live lease.
Expired/released focus needs a fresh lease UUID. Acquisition also returns the
server clock and the project's latest sequence, which the client must catch up
through before enabling editing. A new request includes a fixed `AcquireBefore`
deadline no more than two minutes ahead, preserved on retries. Verified clients
obtain the database clock through `GetFieldLeaseTime` before creating that
deadline, so an inaccurate device clock cannot prevent acquisition. This exposes
server time, not additional user or project data. Expired requests
cannot acquire a lease. `field_lease_attempts` retains project/device/lease UUIDs
only until that deadline, including denied attempts and releases received before
acquisition, so delayed requests cannot resurrect an earlier focus after cleanup.
This fences retries of the original request. A new attempt must use a fresh
lease UUID and deadline. Changing the deadline alone cannot bypass a retained
attempt. After retention expires, the original deadline still rejects an old
request; the server does not keep permanent lease-UUID history.
Renewal, editing, and release of an existing lease do not require an acquisition
deadline. Cancellation received before acquisition must supply the original
deadline so the server can retain its replay barrier until that time.
There are at most 256 retained attempts per device and project; excess requests
fail explicitly. Cleanup runs every minute and on field operations. A retry of a
still-live lease returns its current grant. Field operations take a shared account
lock to protect device cutover, allowing edits in independent projects to proceed
concurrently. `PushFieldEdit` atomically validates the lease
and persists an opaque encrypted update. Clients flush before releasing focus;
late edits after expiry are rejected. Identical already-persisted retries return
their original sequence. A lost connection leaves a bounded lease, and a new
epoch invalidates old leases. This supplements encrypted document merging; the
server does not interpret field values or enforce their internal schema.

`PushGuardedFieldEdit` additionally requires a second live lease owned by the
same authenticated device in the same project and epoch. Both leases are checked
inside the transaction that allocates the update sequence. This fences dependent
edits if a client pauses until its coordinating lease expires. The server learns
that two opaque resources participate in one change, without their names or
values. Identical confirmed retries remain idempotent. Clients use this distinct
method so servers that lack support reject the operation rather than ignore its
precondition.

Device authentication adds immutable 32-byte Ed25519 and X25519 public keys to
device records, plus the linking device ID and its public signature when linked.
`GET /devices/registration` returns the authenticated account's internal user
UUID and configured canonical server URL. `PUT /devices/verified` receives those public keys, the new device's
registration signature, and an optional linking device ID/signature. First
enrollment also supplies `RecoveryDevice`: another device's public keys,
self-registration signature and the ordinary device's linking signature. This
exposes the recovery-device association during enrollment, without exposing its
secret. A fresh hub
challenge contains that user UUID, device UUID, canonical server URL and a random per-connection UUID;
it is consumed when a proof is attempted and is never logged. The server uses
NSec only to verify Ed25519 signatures, as permitted by rule 7. No private or
symmetric key, project title, path or content is added to these APIs.

Registration signs `novalist/v1/device-register || server || userId || deviceId || DSK.public
|| DXK.public`. Linking signs `novalist/v1/device-link || linkingDeviceId ||
registrationBytes`. Connection proof signs `novalist/v1/device-connect || server || userId
|| deviceId || challengeNonce`. UUIDs use RFC 9562 big-endian bytes. Keys are
32 bytes and signatures 64 bytes. Domain strings are UTF-8 without terminators.
`server` is a big-endian uint16 byte length followed by the UTF-8 canonical server
URL, with one trailing slash. Administrators configure `Collaboration__ServerUrl`
to the public HTTPS base URL (loopback HTTP is allowed for local tests). It is
never inferred from an untrusted Host header. Clients compare the advertised URL
with their selected endpoint and sign their own canonical endpoint. This prevents
a server from relaying a proof to another server accepting the same OIDC token.
Changing the public endpoint requires clients to select the new canonical URL.

The invitation API additionally stores invitation identifiers, project IDs,
expiry times and the claiming account ID. No project keys, names, paths or
content are part of invitation requests or responses. Invitation identifiers
are access capabilities and must not be included in diagnostics.

The verified `ActivateInvitation` hub operation additionally stores the claiming
device UUID and an opaque 32-byte certificate fingerprint. It never receives or
interprets the certificate or private invitation material. Account admission and
this activation receipt commit together. The same still-active account/device
can retrieve the same receipt after expiry (including when the original reply
was lost); changed fingerprints/devices, revoked devices and removed memberships
are rejected. Revoking a link stops an unclaimed invitation; it does not erase
an already-committed receipt or remove an existing collaborator.

Project names, item names and paths are **not** stored in `projects` or anywhere
else on the server; they live in the encrypted structure doc (§5.1).

### 10.1 Accounts and login

Users log in through an external OIDC provider (Keycloak, authentik). The server
stores no passwords. A user is identified by the token's issuer and `sub` claim,
which never change; email and username are optional, refreshed from the token's
claims, and used to find people when inviting them (§8.5) and to label the
verified owner's member list. Only verified email claims are stored.

OIDC authenticates the **user**, not the device. Verified devices additionally
answer a fresh `GetDeviceChallenge` using `AuthenticateDevice` before project,
update, presence or invitation operations. Public keys cannot be replaced under
an existing device ID or reused under another ID, including revoked records.
Linked device records reference their authorizing device. The first verified registration serializes on the account
and requires fresh MFA bound to both ordinary and recovery public-key records.
The client obtains a five-minute, one-use enrollment challenge, then uses its
nonce for a new authorization-code/PKCE browser login with `max_age=0`.
The server verifies the signed ID token's issuer, native-client audience,
account subject, nonce, expiry, `amr` containing `mfa`, and a single `auth_time`
within the last five minutes. Ordinary bearer access tokens authenticate the
API calls. Registration signatures prove possession of the device keys.
Tokens with multiple audiences also require `azp` matching the native client;
any supplied `azp` must match it. An enrollment challenge is exclusively claimed
during registration, consumed on success, and available again after a failed
transaction until its original expiry.
An existing `cnf.jkt` access-token flow remains accepted, but is no longer the
only enrollment path. See [device-enrollment.md](device-enrollment.md) for client
entry points, provider configuration, challenge limits, and metadata exposure.
Existing registrations can be retried without repeating enrollment or creating
another recovery device. Later devices require a signature from an active
verified device of that account, including its pre-provisioned recovery device.
Revoked device records still prevent bootstrapping a replacement without linking.
Registration/linking, revocation, and legacy eligibility checks plus their
mutations serialize on the same account lock. An already-authorized legacy
request finishes before verified enrollment can commit; it cannot mutate the
account after the cutover using an earlier eligibility check.

Legacy clients continue to work for accounts with no verified devices. Registering
the first verified device opts that account into possession checks and closes its
unverified connections. Retrying successful enrollment repeats that closure
without interrupting authenticated verified devices. Legacy device registration, access and invitation HTTP
endpoints then reject that account. Its invitations use the authenticated hub
methods instead. This prevents falling back to UUID-only access after revocation.

Invitation mutations revalidate the calling device under the account lock.
Hub invitation calls require verified device possession; legacy HTTP calls
retain the legacy-account check throughout their transaction.
Project removal, joining, update fan-out and each catch-up item share a delivery
barrier. Removal holds it through the membership commit and connection group
removal; database writes consistently lock the project before its memberships.
Catch-up reads bounded batches so it can recheck access without an open reader.

The server is pointed at the provider through configuration (environment
variables, never `appsettings*.json`):

- `Authentication__Schemes__Bearer__Authority`: the issuer URL, e.g.
  `https://keycloak.example.com/realms/novalist`. Signing keys are fetched from
  its `.well-known/openid-configuration`.
- `Authentication__Schemes__Bearer__ValidAudiences__0`: the audience the
  provider puts into access tokens for this server, e.g. `novalist-sync`.

Outside Development the server refuses to start without both. Access tokens are
only accepted in the `Authorization` header, never in the URL, so they cannot
end up in access logs.

For native desktop sign-in, admins additionally configure
`Authentication__NativeClient__ClientId` and
`Authentication__NativeClient__Scope` (including `openid` and the provider's
API scopes; `offline_access` enables refresh tokens where supported). Register
this as a public native client using authorization code with S256 PKCE and a
loopback redirect at `http://127.0.0.1:{ephemeral-port}/callback`, following
RFC 8252. No client secret is shipped with the desktop application.

`GET /client-configuration` publishes the protocol version, `authenticationRequired`,
configured issuer URL, native client ID and scopes. These are public service configuration,
not user or project data. The endpoint returns 503 when native sign-in is not
configured; existing authenticated hub clients remain supported. This endpoint
does not create an OIDC provider or register the native client in that provider.

Roles per membership:

- **owner:** adds and removes members, rotates epochs, edits
- **contributor:** edits
- **viewer:** reads only; the server rejects pushes from viewer devices

## 11. Decision log

| #   | Decision                | Choice                                                                                         |
| --- | ----------------------- | ---------------------------------------------------------------------------------------------- |
| D1  | Repository layout       | Two repos: Novalist fork (incl. Sync.Client) + novalist-sync-server (incl. Contracts as NuGet) |
| D2  | Integration             | Fork Novalist, keep changes small, offer upstream as PR                                        |
| D3  | Where CRDT + crypto run | _Revised 2026-10-19:_ CRDT in JavaScript (Yjs, §4.3); crypto, keys, SignalR in .NET. No YDotNet. Renderer and Yjs host get no keys. (Was: all in .NET with YDotNet.) |
| D4  | Sync scope              | Whole project: scenes live, Codex/metadata as CRDT maps, files as encrypted blobs              |
| D5  | History for new members | Full history by default (all epoch keys wrapped)                                               |
| D6  | Recovery                | Recovery key in v1, implemented as a derived recovery device                                   |
| D7  | Key rotation            | On member removal, device revocation, and every 30 days                                        |
| D8  | Version history         | Tiered snapshots: hourly 24 h, daily 30 d, weekly after                                        |
| D9  | Snapshot trigger        | Every 500 updates per item, or on project close                                                |
| D10 | Forward secrecy         | Not in v1 (rationale §1); rotation + compaction instead                                        |
| D11 | Login                   | External OIDC provider (Keycloak, authentik); users identified by issuer + subject (§10.1)     |
| D12 | Sync is optional        | Per project, off by default; local-only projects need no account, server or network (§5.4)    |
| D13 | Editor                  | Keep the existing editor with a custom `NovalistYjsBinding`; no ProseMirror/Tiptap rewrite    |
| D14 | Where Yjs runs          | `YjsDocumentManager` in an Electron `utilityProcess`, independent of windows and iframes       |
| D15 | Durability              | Durable Yjs log + outgoing queue in the .NET client's SQLite; saved = stored in one transaction |
| D16 | Synced project state    | `Y.Doc` is authoritative, files are its projection; never rebuilt from HTML alone (§5.2)      |
| D17 | Server-side CRDT        | None: no Hocuspocus, no Node.js server, no server-side merging                                 |

## 12. Open questions

- [ ] **Git coexistence:** a `git pull` on a synced project arrives as outside
      file changes and is merged into the CRDT (§5.2). Is that wanted, or should
      Git be discouraged / disabled for synced projects?
- [ ] Should the server verify Ed25519 signatures (public keys only) to drop
      spam from non-member devices early?
- [ ] **Editor schema** (first task of phase 2): exact block types, paragraph
      styles, inline marks, and how comments, footnotes, suggestions, mentions
      and images are represented in the `Y.XmlFragment` (needs a read of the
      editor, sanitizer and `scene-html.js`).
- [ ] **Codex sections** use CodeMirror: bind them with the established
      `y-codemirror.next` binding, or treat them as plain `Y.Text` diffs like
      outside edits?
- [ ] **Conflict resolution UI** for outside changes that cannot be merged
      (§5.2): how conflict copies are named and shown.
- [ ] Mobile (`Novalist.Mobile`) support later?
- [ ] **Device authentication:** OIDC only proves the user. A stolen device with
      a valid session could claim another device ID of the same user, so
      revoking it would not lock it out. Devices should prove possession of
      their Ed25519 key (server challenge, signature verified against the stored
      public key, rule 7) before identity keys ship (phase 4). Consider deriving
      `deviceId` from the Ed25519 public key (e.g. the first 16 bytes of its
      SHA-256). That binds the ID to one key, so it cannot be registered with
      a different key, but it does not prove possession: anyone can hash a
      public key, so the challenge is still required.
- [ ] **Roles on the client:** a viewer holds the epoch keys and can sign valid
      updates, and a compromised server could relay them. Clients must reject
      updates from viewer devices too, which needs a member/role list the server
      cannot forge (e.g. signed by an owner).

## Relay-only desktop integration

The Novalist desktop integration now uses the live packet relay described in
[relay.md](relay.md). Enable it with `Relay:Enabled=true`; it supersedes this document's
storage and offline-joining design for that integration. It maps no persistence
endpoints and requires no database. The preceding design remains documentation
for persistence mode, which remains the default (`Relay:Enabled=false`).

Relay authentication is optional through `Relay:RequireAuthentication=false`;
the default remains true. Anonymous hosts prove route ownership with a random
session capability in the `X-Novalist-Relay-Host` request header. The server learns
that capability and retains only its SHA-256 hash in memory for the session's
lifetime. This adds transient relay metadata, not project encryption keys or
participant names. See [relay.md](relay.md) for discovery, transport requirements
and ownership checks. The legacy persistence mode's authentication is unchanged.

## 13. Future work

- MLS (RFC 9420) for forward secrecy and post-compromise security of key
  distribution.
- Ciphertext padding to reduce size leakage.
- Key transparency to detect key substitution without manual verification.
- Re-encrypting blobs on member removal.
- **Multiple server instances.** v1 runs as a single instance: live broadcasts
  and cutting off open connections on revocation (rule 4) only reach
  connections on the same instance, tracked in an in-memory registry. Scaling
  out needs a Redis backplane for SignalR (broadcasts and group changes across
  instances), revocation published as an event that every instance applies to
  its own local registry, and sticky sessions at the load balancer. Revocation
  and broadcasting each go through a single place in the code, so this only
  replaces those two.

## 14. Build order

Done: **server skeleton + Contracts** (hub, OIDC login, membership, PostgreSQL
log, catch-up, revocation on open connections). Plaintext payloads for now; the
server treats them as opaque either way.

The client is built incrementally. At every step the existing standalone
application keeps working, and the current editor path stays the production
default until the binding is validated.

1. **CRDT foundation.** Add Yjs and the Yjs host (`utilityProcess`) with
   `YjsDocumentManager`. Import and project basic scene content (HTML ⇄
   `Y.XmlFragment`) and check that the round trip keeps existing scenes
   unchanged. No editor or sync changes yet.
2. **Editor integration.** Define the editor schema (§12). Build
   `NovalistYjsBinding`: precise text operations, paragraphs, formatting,
   collaborative undo, cursors. Prove that concurrent edits converge.
3. **Complex features.** Adapt suggestions, comments, footnotes, mentions and
   dictation. Separate grammar highlighting and pagination from document
   content. Keep the current editor UI and formatting behavior.
4. **Persistence and synchronization.**
   1. Durable local Yjs log and outgoing queue in the .NET client (§5.5).
   2. Plaintext sync through the existing server: offline edits, reconnect,
      catch-up, pause/resume.
   3. Enable sync and make local copy (§8.1, §8.10).
   4. Encryption with a single epoch key, sealed envelopes.
   5. Identity keys, device authentication, wrapping, signatures, membership,
      safety numbers.
   6. Whole project: structure doc, Codex, file manifest + blobs.
   7. Snapshots, compaction, tiered retention.
   8. Epochs: removal, revocation, monthly rotation.
   9. Recovery key, device linking.
5. **Stabilization.** Regression tests against existing projects and editor
   behavior; crash recovery, offline editing and concurrent modification tests;
   verification that enabling sync never changes content.

### 14.1 Acceptance tests

The client is done when it demonstrates all of these:

1. An existing Novalist project opens and works without synchronization.
2. Local-only editing needs no account or network connection.
3. Existing HTML/JSON projects keep their content and supported formatting.
4. Two clients can insert and delete text concurrently without losing edits.
5. Updates converge regardless of delivery order.
6. Offline changes merge correctly after reconnecting.
7. Restarting Novalist keeps every durably saved but unsynchronized change.
8. Undo never removes another collaborator's edits.
9. Comments, suggestions, formatting and cursor positions stay consistent under
   concurrent changes.
10. Grammar highlighting and pagination never produce CRDT updates.
11. Outside filesystem changes never silently destroy synchronized content.
12. The server never receives plaintext or keys.
13. Pause/resume never discards queued updates.
14. A local copy does not affect the shared original.
15. Existing non-sync editor behavior keeps working throughout the migration.

Tests 4, 5, 8 and 9 are backed by randomized convergence tests: several Yjs
documents, random concurrent edits through the binding, delivery in random
order, all documents and projections identical at the end.
