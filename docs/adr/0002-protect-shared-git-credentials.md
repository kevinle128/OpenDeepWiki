---
status: accepted
---

# Protect shared Git credentials as server-only secrets

GitHub and GitLab personal access tokens are encrypted at rest and can never be read back through the user interface or API.
Credential replacement validates the new token before an atomic update, so an invalid replacement does not interrupt the active connection.
GitLab self-hosted connections require HTTPS with a valid certificate; OpenDeepWiki does not provide an insecure TLS bypass because a shared credential grants access to private source code.

## Consequences

Audit events record connection and repository actions without credential values.
Disabling a connection stops discovery, new indexing, and synchronization while existing documentation remains readable.
