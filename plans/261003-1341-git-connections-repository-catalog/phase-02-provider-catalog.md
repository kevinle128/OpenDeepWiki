---
title: "Phase 2: Provider catalog"
status: completed
phase: 2
plan: "git-connections-repository-catalog"
depends_on: [1]
scope: "backend API, provider HTTP, SSRF controls"
test_strategy: "TDD with fake HTTP handlers, endpoint integration tests, and relational constraints"
---

# Phase 2: Provider catalog

## Context

Deliver PAT connection CRUD and paged repository and branch discovery for GitHub.com, GitLab.com, and HTTPS self-hosted GitLab.
Use [provider research](./research/researcher-02-git-pipeline.md), [UI/API research](./research/researcher-03-ui-api-tests.md), and the [Phase 2 scout report](./reports/scout-phase-02-provider-catalog.md).
This phase does not create indexes or change GitHub App imports.

### Inherited from Phase 1

- Soft-delete connections only. EF clears the connection reference of tracked repositories when a tracked connection is removed, even with `Restrict`.
- `GitConnection.Version` is never filled by either provider. Use an explicit concurrency token to return 409 on conflicting updates.
- `GitConnection.Restore()` does not re-enable a disabled connection. Decide and test that policy here.
- `GitConnections.Id` has no length limit but referencing columns are 36 characters; generate GUID IDs.
- Add a PostgreSQL test for soft-deleted identity restore.
- PostgreSQL tests run only when `OPENDEEPWIKI_TEST_POSTGRES` is set.

## Requirements

- Every authenticated user can list, create, and use shared connections.
- Only creator/Admin can rename, test, rotate, enable, disable, or delete a connection.
- Validate PATs before storage and derive `ExternalAccountId` from the provider.
- GitHub uses numeric repository IDs and GitLab uses numeric project IDs.
- Return opaque cursors bound to provider, connection, and allowed API origin.
- Public, valid HTTPS GitLab hosts work by default.
- Private-network GitLab hosts or CIDRs work only when an operator allowlist permits them.
- Revalidate DNS results and redirect targets to stop SSRF and DNS rebinding.
- Never bypass TLS certificate validation.
- Authenticate or retire the legacy branch-discovery route so it cannot use server-side provider tokens for anonymous callers.
- Deleting a referenced connection returns 409.
- Disabling preserves repositories and documentation but blocks discovery and later write work.

## File inventory

| Path | Action | Rough size | Test impact |
|---|---|---:|---|
| `src/OpenDeepWiki/Services/GitConnections/IGitProviderClient.cs` | Create validate, get repository, list repositories, and list branches contract. | S | Contract fakes. |
| `src/OpenDeepWiki/Services/GitConnections/GitProviderModels.cs` | Create provider DTOs, page type, and stable errors. | M | Serialization tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitProviderClientResolver.cs` | Select GitHub or GitLab implementation. | S | Unsupported provider tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitHubPatProviderClient.cs` | Add `/user`, repository, branch, and Link pagination calls. | L | Fake-handler tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitLabPatProviderClient.cs` | Add `/api/v4/user`, project, branch, keyset, and offset fallback calls. | L | Fake-handler tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitLabServerUrlValidator.cs` | Normalize HTTPS origins and enforce address policy. | M | SSRF matrix tests. |
| `src/OpenDeepWiki/Services/GitConnections/ProviderPaginationCursorCodec.cs` | Sign or protect origin-bound opaque cursor data. | M | Tamper and cross-connection tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitConnectionService.cs` | Add CRUD, validate-before-write, uniqueness restore, dependency conflict, and audit. | L | Service tests. |
| `src/OpenDeepWiki/Endpoints/GitConnectionEndpoints.cs` | Add authenticated connection, catalog, branch, health, enable, disable, and audit routes. | M | HTTP status and DTO tests. |
| `src/OpenDeepWiki/Models/GitConnections/GitConnectionModels.cs` | Add request and safe response DTOs with `hasSecret` only. | M | Secret serialization tests. |
| `src/OpenDeepWiki/Program.cs` | Register named clients, validators, allowlist options, and endpoints. | M | Startup tests. |
| `src/OpenDeepWiki/appsettings.json` | Add empty operator-managed private-host/CIDR allowlist and timeouts. | S | Configuration tests. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitHubPatProviderClientTests.cs` | Create GitHub validation and pagination tests. | L | New red tests. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitLabPatProviderClientTests.cs` | Create SaaS and self-hosted tests. | L | New red tests. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitLabServerUrlValidatorTests.cs` | Create URL, DNS, CIDR, redirect, and rebinding tests. | L | New red tests. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitConnectionServiceTests.cs` | Create lifecycle, uniqueness, and dependency tests. | L | New red tests. |
| `tests/OpenDeepWiki.Tests/Endpoints/GitConnectionEndpointsTests.cs` | Create auth, capability, response, and error-code tests. | L | New red tests. |

Do not add provider SDKs or a retry package.
Use `IHttpClientFactory`, `System.Text.Json`, and current platform APIs.

## Test scenario matrix

| Area | Red test | Expected result |
|---|---|---|
| GitHub identity | Valid PAT calls `/user` | External numeric account ID and login are trusted. |
| GitLab identity | GitLab.com or allowed self-hosted PAT calls `/api/v4/user` | External numeric account ID and username are trusted. |
| Global identity | Second token resolves to the same provider account | Existing connection is returned and its credential is not replaced without maintenance authority. |
| GitHub paging | More than 100 repositories or branches | Follow validated `Link` pages without duplicates. |
| GitLab paging | Keyset supported | Follow validated next links. |
| GitLab fallback | Keyset unsupported | Use offset paging without losing items. |
| GitLab subgroup | Nested project path | Branch request uses numeric project ID. |
| SSRF default | Public HTTPS host resolves only to public addresses | Allowed. |
| SSRF private | Host resolves to private, loopback, link-local, or metadata address | Rejected unless the exact private host/CIDR is operator-allowed. |
| SSRF redirect | Allowed origin redirects to a blocked address | Rejected before credentials are sent. |
| Cursor | Cursor is changed or used with another connection | 400 without outbound request. |
| Failure mapping | 401, 403, 404, 429, 5xx, DNS, TLS, or timeout | Stable safe error code; no raw body. |
| Lifecycle | Disable a referenced connection | Existing docs remain; discovery returns `CONNECTION_DISABLED`. |
| Delete | Repository depends on connection | 409 and no deletion. |

## Function and interface checklist

- [x] `IGitProviderClient.ValidateAsync` returns trusted account identity.
- [x] `GetRepositoryAsync` refreshes mutable metadata by stable remote ID.
- [x] `ListRepositoriesAsync` and `ListBranchesAsync` honor cancellation and page limits.
- [x] `GitLabServerUrlValidator` rejects HTTP, userinfo, path, query, fragment, invalid port, and blocked addresses.
- [x] Each redirect target is validated or redirects are disabled.
- [x] `ProviderPaginationCursorCodec` binds provider, connection, origin, and allowed pagination fields.
- [x] `GitConnectionService.CreateAsync` validates before protection and persistence.
- [x] `UpdateAsync` validates replacement credentials before an atomic swap.
- [x] `DeleteAsync` enforces creator/Admin and dependency conflict.
- [x] Response DTOs never contain `token` or `ProtectedToken`.

## Dependency map

```text
GitConnectionEndpoints -> GitConnectionService -> authorization + protector
GitConnectionService -> provider resolver -> GitHub/GitLab clients -> named HttpClient
GitLab client -> URL validator -> DNS/address allowlist -> redirect validation
Provider clients -> cursor codec -> catalog responses
```

## Tests Before

1. Add fake-handler tests for every provider request and failure class.
2. Add SSRF tests for public hosts, private allowlists, mixed DNS answers, rebinding, redirects, and metadata IPs.
3. Add endpoint tests for 401, creator/Admin maintenance, shared use, dependency 409, and secret-free JSON.
4. Confirm all new tests fail because the provider and API contracts do not exist.

## Refactor and implementation

1. Add the small provider contract and shared page/error models.
2. Implement URL safety before any self-hosted request code.
3. Implement GitHub and GitLab clients with validated pagination.
4. Implement connection service lifecycle and audit writes.
5. Add authenticated endpoints and stable error mapping.
6. Keep GitHub App route and UI contracts compatible, but remove anonymous credential-bearing discovery and query-string token behavior from legacy provider paths.

## Tests After

1. Run provider tests after each endpoint family becomes green.
2. Verify canary PAT absence in requests captured after redirects, logs, exceptions, DTO JSON, and audit metadata.
3. Run concurrency tests for duplicate account creation and credential rotation.
4. Run the endpoint suite with authenticated user, creator, Admin, and anonymous identities.

## Regression gates

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitHubPatProviderClientTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitLabPatProviderClientTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitLabServerUrlValidatorTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitConnectionServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitConnectionEndpointsTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
```

## Security and risk

- Resolve all DNS answers and reject the request if any selected address violates policy.
- Apply allowlists at request time, not only at connection creation.
- Send PATs only in provider headers and never in URLs.
- Honor `Retry-After` without an unbounded endpoint retry loop.
- Use allowlist configuration as an operator trust decision and never let users edit it.

## Rollback

- Disable new routes while keeping additive schema and encrypted rows.
- Keep old GitHub App and public statistics paths available only where they do not expose private provider data or credentials.
- Do not convert encrypted PATs to plaintext during rollback.

## Todo

- [x] Write provider, SSRF, cursor, service, and endpoint failing tests.
- [x] Implement provider contracts and clients.
- [x] Implement public-default and private-allowlist URL policy.
- [x] Implement connection CRUD, disable, health, delete conflict, and audit.
- [x] Add authenticated endpoints and safe DTOs.
- [x] Pass focused and full regression gates.

## Success criteria

- [x] GitHub.com, GitLab.com, and allowed HTTPS self-hosted GitLab catalogs page completely.
- [x] Public HTTPS hosts work by default and private hosts work only through the operator allowlist.
- [x] Stable identities survive repository rename or transfer.
- [x] Connection maintenance, shared use, disable, and dependency-delete rules pass.
- [x] No TLS bypass, arbitrary cursor URL, provider SDK, or secret disclosure exists.
