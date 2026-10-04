# OpenDeepWiki Context

OpenDeepWiki builds and maintains searchable documentation from shared source repositories.

## Language

**Shared Workspace**:
The single collaboration boundary that contains all users, Git connections, repositories, and indexed documentation in one OpenDeepWiki deployment.
Every user inside this boundary can discover private repositories, register repositories, and manage indexed branches through available Git connections.
_Avoid_: Organization, tenant

**Git Connection**:
A reusable authorization relationship between the Shared Workspace and one Git provider account.
All users can use it, while only its creator or an administrator can maintain it.
_Avoid_: Repository credential, user token

**Connection Maintainer**:
The user who created a Git Connection, or an administrator acting on behalf of the Shared Workspace.
_Avoid_: Connection owner, token owner

**Disabled Connection**:
A Git Connection that preserves existing indexed documentation but cannot discover repositories, create indexes, or synchronize branches until re-enabled.
_Avoid_: Deleted connection, disconnected repository

**Connected Repository**:
A remote Git repository selected through a Git Connection and registered for documentation indexing.
Each remote repository has at most one active registration in the Shared Workspace.
_Avoid_: Imported account, cloned project

**Indexed Branch**:
A selected branch of a Connected Repository whose documentation OpenDeepWiki generates and keeps synchronized.
Its indexing and synchronization lifecycle is independent from other branches of the same repository.
_Avoid_: Repository copy, separate repository
