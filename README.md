# Novalist Sync Server

End-to-end encrypted collaboration for Novalist. The server forwards opaque
packets or stores encrypted updates; project content and encryption keys stay
with clients.

- [Self-hosting and Docker images](docs/self-hosting.md)
- [Production Compose examples](deploy/README.md) — relay or persistent server, HTTPS, OIDC and PostgreSQL
- [Live relay mode](docs/relay.md) — used by the current desktop integration
- [Persistent protocol design](docs/design.md)
- [TLS, proxies and limits](docs/transport.md)
- [Database migrations](docs/migrations.md)
- [Local development and packages](docs/local-packages.md)

Server: AGPL-3.0-or-later. Contracts: MIT. See [LICENSE](LICENSE).
