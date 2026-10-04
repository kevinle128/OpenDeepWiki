---
status: accepted
---

# Share Git connections in one workspace

OpenDeepWiki treats one deployment as one Shared Workspace.
Any user can create a Git Connection, and all users can use that connection to discover and index repositories.
Only the connection creator or an administrator can replace its credential, disable it, or delete it.
We do not introduce Organization or Department ownership because all current users collaborate inside the same trust boundary; adding tenant boundaries now would add authorization and migration complexity without a current product need.

## Consequences

Git credentials remain server-side and unreadable after storage.
A connection cannot be deleted while a Connected Repository depends on it, but a maintainer can disable it immediately.
A remote repository has one active registration in the Shared Workspace; selecting it again opens branch management instead of creating a duplicate.
All users can discover private repositories and manage indexed branches through an active connection.
Connections are unique by provider, server, and provider account; a duplicate attempt redirects to the existing connection and never replaces its credential.
A selected branch synchronizes automatically and fails independently from sibling branches.
Existing repository credentials migrate into shared connections and are removed from repository records only after a verified backup and successful relinking.
A future multi-organization requirement will need an explicit ownership migration for connections, repositories, and indexed documentation.
