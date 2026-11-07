# Novalist Sync

Novalist Sync connects collaborators while keeping their project content encrypted.

## Language

**Recovery device**:
A separately provisioned device identity, or one recreated from an account's
recovery key, that can authorize a replacement when ordinary devices are lost,
provided the recovery device has not been revoked.
Enrollment recovery alone does not restore project content or missing project keys.

**Project epoch**:
A version of a shared project's encryption keys and authorized devices. Epoch 0
starts when the project is created; later epochs start when the owner commits
a key rotation. A rotation that removes a collaborator
excludes their devices from the new epoch.

**Field editing lease**:
A temporary right for one collaborator's device to edit a shared field. Other
devices wait until that right is released or expires.
