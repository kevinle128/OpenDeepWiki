# Phase 2 cook report: provider catalog

Status: DONE_WITH_CONCERNS. All Phase 2 requirements are implemented. The only failing tests are the 7 baseline failures. The concerns are listed at the end.

## Results

- Focused gates: GitHub client 48 passed, GitLab client 39, URL validator 78, connection service 56, endpoints 47. Extra classes: cursor codec 20, startup and configuration 9. No failures.
- Full suite (`OpenDeepWiki.Tests`), final run without PostgreSQL: 1005 total, 991 passed, 7 failed, 7 skipped (the PostgreSQL tests). The 7 failures are exactly the baseline list (6 `RepositoryAnalyzerSourceTests`, 1 `RepositorySkillMarkdownBuilderTests`); checked with a diff against `baseline-failing-tests.txt`.
- Full suite with PostgreSQL, run before the last service fix (which added one service test): 1004 total, 997 passed, 7 failed (same baseline list), 0 skipped. PostgreSQL ran against a disposable `postgres:16-alpine` container on port 55434 with `OPENDEEPWIKI_TEST_POSTGRES` set; the container is removed. This includes the new soft-delete restore test and the new concurrency stamp test. The last service change touches no PostgreSQL code path.
- `dotnet build OpenDeepWiki.sln`: 0 errors. A `--no-incremental` build shows CS1591 documentation warnings; they come from Phase 1 entity files and an older `ChatApp` member, not from this phase.
- Mutation check: I removed the next-link origin check and the maintainer check on the health test. The suites failed as expected. The code is restored.
- TDD order. The schema, service, endpoint and startup families were confirmed red first (compile errors for missing members, and one failing assertion for the legacy route). For the URL validator, cursor codec and the two provider clients I wrote the tests first but built tests and code together, so I did not capture a separate red run for those four families.
- Review fix: a request that sent a rename and an invalid replacement token together persisted the rename. `UpdateAsync` now calls the provider first and mutates only after it succeeds. The new test `Update_WhenRenameAndInvalidTokenAreSentTogether_PersistsNeither` failed before the fix (display name was "Renamed") and passes now.
- One unreproduced failure: `Create_ReturnsSafeResponseThatNeverContainsTheToken` failed once in the first full run, with no failure text kept. The test read the SQLite file while a service context was still open. It now closes all contexts and clears the connection pools first, as the Phase 1 model test does. It did not fail in 10 later runs. Re-run it in CI to be sure.

## What changed

Schema (additive, both providers): `GitConnection` gets `DisplayName` (needed to rename) and `ConcurrencyStamp` (explicit token, `IsConcurrencyToken`). One migration per provider, `AddGitConnectionDisplayNameAndConcurrencyStamp`, generated with the local `dotnet-ef` tool. `has-pending-model-changes` is clean for both. `DbInitializer` creates the columns in the new-table DDL and adds them to existing tables (`ADD COLUMN IF NOT EXISTS` and the SQLite helper). Both columns have an empty default so old rows upgrade. The schema parity tests pass for new, upgraded, and migrated databases. `GitConnectionAuditEventType.Renamed` is added (stored as an integer, no schema change).

Provider layer (`Services/GitConnections/`):

- `IGitProviderClient`, `IGitProviderClientResolver`, `GitProviderModels` (DTOs, stable `PROVIDER_*` codes, options), `GitHubPatProviderClient`, `GitLabPatProviderClient`. No SDK and no retry package. Tokens go in headers only (`Authorization: Bearer` for GitHub, `PRIVATE-TOKEN` for GitLab).
- Repositories and branches are paged by numeric ID. GitHub uses `/user/repos` and `/repositories/{id}/branches`. GitLab uses `/projects` and `/projects/{id}/repository/branches`, so subgroup paths and renames never matter. GitLab tries keyset paging first and falls back to offset paging when the first page is rejected with 400. `X-Next-Page` and `X-Total` are honored.
- Pagination links from the provider are never followed or stored. The client checks that the link is HTTPS, has the same origin and the same path, and copies only whitelisted pagination fields (`page`, `id_after`, `page_token`, `pagination`, `order_by`, `sort`) with strict value shapes into the cursor. The request URL is always built from the fixed base.
- `ProviderPaginationCursorCodec` protects the cursor with Data Protection (purpose `OpenDeepWiki.GitConnections.Cursor.v1`). It binds provider, connection ID, API origin, list kind, and repository scope. The check runs before any outbound request.
- `GitLabServerUrlValidator` normalizes an HTTPS origin and applies the address policy. `GitProviderGuardHandler` is a `DelegatingHandler` in front of the GitLab primary handler. For every request it checks the allowed origin, resolves DNS once, rejects the request if any answer is not allowed, and pins the validated addresses on the request. The primary `SocketsHttpHandler` connects only to the pinned addresses (`ConnectCallback`), with automatic redirects off, cookies off, and default certificate validation. A 3xx answer becomes `PROVIDER_REDIRECT_BLOCKED`, so a redirect target never receives the token. The GitHub client uses a plain handler with the same redirect and TLS settings.
- Failure mapping: 401, 403, 403 with exhausted rate limit, 404, 429, 5xx, 3xx, DNS, TLS, timeout, invalid JSON, oversized body (8 MiB cap) all map to stable codes. `Retry-After` and `X-RateLimit-Reset` are reported on the exception (capped at one hour) and never slept on. Logs hold only provider, connection ID, code, and status.

Service and API:

- `GitConnectionService` (CRUD, health test, enable and disable, audit list, catalog) and `GitConnectionEndpoints` under `/api/v1/git-connections`. Routes: list, create, get, update (PUT), delete, `test`, `enable`, `disable`, `repositories`, `repositories/{id}/branches`, `audit-events`. The group requires authorization. Envelope: `{ success, data }` or `{ success: false, errorCode, message }`, with fixed messages per code.
- `Program.cs` calls the new `AddGitProviderCatalog` extension. `appsettings.json` has a `GitProviders` section with `RequestTimeoutSeconds`, an empty `AllowedPrivateHosts`, and an empty `AllowedPrivateCidrs`. Options are validated at startup (invalid CIDR or timeout outside 1 to 120 fails).
- Legacy route `GET /api/v1/repositories/branches` now has `[Authorize]`. The web helper `fetchGitBranches` in `web/lib/repository-api.ts` now goes through the shared `api` client, so a signed-in browser still sends its token (the old helper only added a token on the server, so without this change the branch picker would have broken). `GitPlatformService` no longer sends the Gitee token in the query string; Gitee calls are anonymous (I could not verify a header form, so I did not guess).

## Decisions the orchestrator must know

1. Restore policy (the Phase 1 open item). A create that matches a soft-deleted identity restores that row and keeps one row. The freshly validated token replaces the stored one, the caller becomes the creator, the display name is refreshed, and a `Restored` audit event is written. A disabled connection stays disabled: restore never re-enables use, and the new creator can enable it. `GitConnection.Restore()` is unchanged and its Phase 1 test still passes. Tests lock all of this.
2. Create never replaces a credential. A create for an account that already has a live connection returns that connection with HTTP 200 and `existing: true` and drops the validated token. Rotation is `PUT` with a token.
3. Delete is a soft delete and scrubs `ProtectedToken` to an empty string, so a deleted row holds no usable secret. It returns 409 `CONNECTION_IN_USE` while a non-deleted repository references the connection. The dependency check and the soft delete are not one atomic step against a concurrent repository assignment (the stamp does not change when a repository is assigned). Phase 3 must reject an assignment to a deleted or disabled connection inside its own transaction.
4. Address policy. Public addresses pass. Private ranges (RFC 1918, CGNAT, unique local, loopback, benchmarking) pass only when the exact host is in `AllowedPrivateHosts` or the address is inside an `AllowedPrivateCidrs` entry. Link-local (including 169.254.169.254 and `fe80::/10`), unspecified, multicast, reserved, Teredo, `100.100.100.200`, and `fd00:ec2::254` are never allowed, even when listed. This is stricter than the matrix wording "unless operator-allowed" for the metadata case; I chose it because no real GitLab lives on a metadata address. IPv4 embedded in mapped, compatible, NAT64, and 6to4 IPv6 forms is unwrapped before the check.
5. Provider token rejection returns 422, not 401, because the web client treats any 401 as an expired session and signs the user out.
6. Audit events are readable by the creator or an Admin only. The audit list takes `limit` (default 30, max 100), newest first, with no cursor. `Used` is not written for each catalog page (that would flood the table); `UseDenied` is written when a disabled connection is used.
7. Health test and rotation are allowed on a disabled connection (they are maintenance, not discovery). A failed replacement writes a failure audit event and leaves the active credential and its health untouched. A replacement for another provider account returns 409 `CONNECTION_ACCOUNT_MISMATCH`.
8. Concurrency: every mutation sets a new `ConcurrencyStamp`. A second writer that read the old row gets 409 `CONNECTION_CONFLICT` (tested on SQLite and PostgreSQL, and through the service with an interleaved rotation). A lost insert race on the identity index re-reads once and returns the winner. Two concurrent restores of one row return 409 to the loser.
9. The catalog does not yet return the local "already imported" state; repository identity columns arrive in Phase 3.
10. The Phase 1 test that required the last migration to be `AddGitConnections` now marks everything before `AddGitConnections` as applied and runs both Git connection migrations.

## Files outside the phase inventory (added or touched)

Source: `Services/GitConnections/GitProviderGuardHandler.cs`, `GitProviderClientBase.cs`, `GitConnectionServiceCollectionExtensions.cs` (new); `RepositoryService.cs` (`[Authorize]`); `GitPlatformService.cs` (Gitee token); `Infrastructure/DbInitializer.cs`; `OpenDeepWiki.Entities/GitConnections/GitConnection.cs` and `GitConnectionAuditEvent.cs`; `OpenDeepWiki.EFCore/MasterDbContext.cs`; the two new migrations with designers and both model snapshots (tool output); `web/lib/repository-api.ts`.
Tests: `GitProviderTestDoubles.cs`, `ProviderPaginationCursorCodecTests.cs`, `GitProviderStartupTests.cs` (new); `EFCore/GitConnectionModelTests.cs` and `Infrastructure/DbInitializerGitConnectionTests.cs` (Phase 1 files, extended).
Not touched: `README.md`, `.env.example`, compose files, `plan.md` status cell (the plan hook says not to edit it directly).

## Verification gaps

- No live provider or real DNS and TLS handshake was exercised. GitHub's `GET /repositories/{id}` and `/repositories/{id}/branches` (numeric-ID addressing) are not listed in the public REST reference, so Phase 6 should exercise them with a real token.
- The legacy-route test checks that `[Authorize]` is on the method by reflection, not that MiniApis enforces it. The repo already relies on the same attribute on `RepositoryDocsService.ExportAsync`. The connect-callback is tested against a loopback TCP listener, and the handler settings are asserted, but a TLS failure against a bad certificate is simulated with a fake handler.
- The web change could not be type-checked or linted: `web/node_modules` is not installed in this checkout. The edit is one function and uses the existing `api.get` signature.
- The new routes are not in any OpenAPI or docs page. Phase 6 should document `GitProviders:*` settings and the allowlist.

## Unresolved questions

- Should audit events be visible to every signed-in user instead of maintainers only? I chose the narrower rule; widening it is a one-line change.
- Is a Gitee token header form wanted later? It was removed from the query string and not replaced.
