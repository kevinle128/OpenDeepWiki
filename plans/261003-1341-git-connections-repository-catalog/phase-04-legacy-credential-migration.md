---
title: "Phase 4: Legacy credential migration"
status: in-progress
note: "Expand, backfill, and switch are delivered and tested. The contract step is authored and tested on copies but not applied or enabled; legacy columns and fields stay in the model, schema, and DbInitializer. See reports/cook-phase-04-report.md."
phase: 4
plan: "git-connections-repository-catalog"
depends_on: [1, 2, 3]
scope: "expand, backfill, switch, contract"
test_strategy: "TDD with idempotent migration service tests, SQLite/PostgreSQL upgrade tests, and recovery drills"
---

# Phase 4: Legacy credential migration

## Context

Move `Repository.AuthAccount` and `Repository.AuthPassword` into shared protected connections without exposing or losing credentials.
Use the [identity research](./research/researcher-01-identity-security.md) and [Phase 4 scout report](./reports/scout-phase-04-migration.md).
Perform expand, backfill, switch, and contract as separate deployment gates.

### Inherited from earlier phases

- Phase 3 already adopts a legacy repository with the same normalized clone URL into a connection (`ConnectedRepositoryService`) and leaves its plaintext fields in place. The backfill must reuse that adoption path and must not add a second relinker. The user may still reject this adoption; keep it behind one seam.
- `GitConnection.ConcurrencyStamp` is the concurrency token. Do not change creator-on-restore behavior; it awaits a user decision.
- Gitee and unknown-host repositories remain unsupported legacy islands. Their Gitee token handling is an open user decision.
- Operate only on copies of `data/opendeepwiki.db` in the scratchpad. The original belongs to a running application.

### Session boundary

Deliver expand, backfill, and switch with tests in this session. Author the contract migration (including the SQLite table rebuild), its tests on copies, and a runbook, but do not apply or enable it. The contract gates (verified SQLite and PostgreSQL backups, key-ring backup, backfill proof, one full update cycle, no Gitee or unknown-host repository depending on legacy columns) cannot be satisfied in this session. Keep the legacy columns in the shipped model and schema.

## Requirements

- Back up and restore-test the database and Data Protection key ring before backfill and contract.
- Keep dual-read only in `IGitCredentialResolver`.
- Prefer `GitConnectionId` and never fall back when a referenced connection is disabled, deleted, corrupt, or invalid.
- Use legacy fields only when `GitConnectionId` is null.
- Validate legacy PATs through the provider to obtain `ExternalAccountId`.
- Upsert by the global connection key, not by PAT, owner, login, or repository URL.
- Preserve an existing connection creator.
- For a new shared connection, select the creator deterministically by earliest repository `CreatedAt`, then repository ID.
- Persist safe migration progress and stable errors without any secret, ciphertext, or provider body.
- Stop all new writes to legacy fields before backfill.
- Remove runtime fields and database columns only after switch gates pass.
- Detect credentials embedded in `GitUrl`, reject new userinfo URLs, redact diagnostics, and place legacy userinfo URLs in the repair queue.
- Classify Gitee and unknown-host repositories as unsupported legacy islands for this feature; preserve their legacy read path and block contract until a separate supported migration exists.
- Keep the legacy submit route shape during expand, but return a stable validation error for new credential fields and direct callers to `GitConnectionId`.
- Require current database Admin status for migration operations.

## File inventory

| Path | Action | Rough size | Test impact |
|---|---|---:|---|
| `src/OpenDeepWiki.Entities/GitConnections/GitCredentialMigrationRecord.cs` | Create per-repository progress, attempts, and safe error state. | S | Idempotency tests. |
| `src/OpenDeepWiki/Services/GitConnections/ILegacyGitCredentialMigrationService.cs` | Create dry-run, batch migrate, and status contract. | S | Admin endpoint seam. |
| `src/OpenDeepWiki/Services/GitConnections/LegacyGitCredentialMigrationService.cs` | Implement deterministic grouping, provider validation, upsert, assignment, verification, and resume. | L | Core migration tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitCredentialResolver.cs` | Implement connection-first, null-only fallback, then remove fallback at contract. | M | Transition tests. |
| `src/OpenDeepWiki/Endpoints/Admin/AdminGitConnectionMigrationEndpoints.cs` | Add explicit Admin-only dry-run, batch, and status operations if CLI hosting is not used. | M | Auth and response tests. |
| `src/OpenDeepWiki/Program.cs` | Register migration service without automatic provider calls at startup. | S | Startup tests. |
| `src/OpenDeepWiki.EFCore/MasterDbContext.cs` | Add progress set and unique repository progress index. | S | Model tests. |
| `src/OpenDeepWiki/Infrastructure/DbInitializer.cs` | Add progress schema and later idempotent contract upgrade for both providers. | L | Upgrade tests. |
| `src/OpenDeepWiki/Models/RepositorySubmitRequest.cs` | Expand with connection ID, stop old writes, then delete legacy fields at contract. | M | API compatibility tests. |
| `src/OpenDeepWiki/Models/Admin/RepositoryModels.cs` | Replace credential replacement with connection reassignment, then remove old fields. | M | Admin tests. |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs` | Stop legacy writes; change `HasPassword` and private access logic to connection semantics. | L | 26 submit and 21 visibility tests. |
| `src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs` | Stop direct legacy credential writes. | M | Admin regression tests. |
| `src/OpenDeepWiki.Entities/Repositories/Repository.cs` | Remove legacy fields only in contract release. | S | Compile and schema tests. |
| SQLite/PostgreSQL `*RemoveLegacyRepositoryCredentials*` migrations | Generate contract migrations; SQLite rebuild must preserve every new column, FK, and index. | L | Provider upgrade tests. |
| Both current model snapshots | Remove legacy columns through EF tooling at contract. | M | Snapshot checks. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/LegacyGitCredentialMigrationServiceTests.cs` | Create migration, resume, dedupe, and redaction tests. | L | New red tests. |
| Existing repository, analyzer, visibility, admin, and initializer tests | Convert fixtures and assertions to connection semantics. | L | Regression coverage. |

Do not modify historical initial migrations.
Do not run network migration automatically at application startup.

## Test scenario matrix

| Area | Red test | Expected result |
|---|---|---|
| Public repository | No legacy credential | Connection remains null and behavior is unchanged. |
| GitHub/GitLab | Valid legacy PAT | Shared protected connection is assigned and verified. |
| Global dedupe | Different PATs or owners resolve to one external account | One connection; all matching repositories link to it. |
| Creator | Existing global connection exists | Existing creator remains. |
| Creator | New global connection is needed | Earliest repository then ID selects creator deterministically. |
| Resume | Failure occurs after part of a batch | Rerun creates no duplicate connection, audit, or assignment. |
| Provider failure | 401, 403, 429, timeout, revoked PAT | Safe progress error; legacy data remains for manual repair. |
| Resolver | Connection ID is valid | Connection credential is used. |
| Resolver | Connection ID is present but unusable | Fail closed; never read legacy PAT. |
| Resolver | Connection ID is null before switch | Legacy path works and emits a safe diagnostic. |
| Secret | Canary PAT crosses every migration failure | Zero match outside the legacy source field before contract. |
| Schema | SQLite table rebuild | All current indexes, FKs, and new columns survive. |
| Contract | Runtime and snapshots are searched | No non-migration `AuthAccount` or `AuthPassword` use remains. |
| Unsupported legacy | Gitee, unknown host, or URL userinfo still needs credentials | Contract is blocked and runtime support is preserved. |
| Recovery | Restore database without matching key ring | Expected failure; joint restore succeeds. |

## Function and interface checklist

- [x] `DryRunAsync` returns counts, provider, state, and safe error codes only.
- [x] `MigrateAsync` uses bounded batches, cancellation, and per-item progress.
- [x] Provider validation happens before a global-key upsert.
- [x] Unique conflicts re-read the existing connection.
- [x] Assignment is verified through the new read-only credential path before success is recorded.
- [x] `IGitCredentialResolver` is the only dual-read location. The migration service also reads the legacy fields, as its source, and never to authenticate.
- [x] New submit and admin paths never write legacy credential fields.
- [ ] Contract removes legacy DTOs, entity properties, resolver fallback, service logic, columns, and current snapshot entries.

## Dependency map

```text
Expand schema + durable key ring
  -> explicit migration service -> provider identity validation -> global connection upsert
  -> repository assignment -> new-path verification -> progress record
  -> switch gate -> one full update cycle -> contract schema
```

## Tests Before

1. Create fixtures from pre-expand SQLite and PostgreSQL schemas with public, valid, shared-account, revoked, and corrupt legacy credentials.
2. Add failing tests for deterministic creator choice, global external-account dedupe, resume, and canary redaction.
3. Add resolver transition tests and contract schema tests before changing runtime code.
4. Capture a contract-before backup and prove the test restore procedure.

## Refactor and implementation

1. Expand with progress storage and connection-first resolver behavior.
2. Stop every new legacy write while preserving read compatibility.
3. Add explicit Admin-operated dry-run, batch, status, and retry controls.
4. Backfill in bounded transactions and verify each assignment through the new path.
5. Switch off fallback only when all active remote repositories are migrated and no unsupported legacy island remains; a blocking record alone does not permit contract.
6. Observe clone, pull, branch list, incremental update, and rebuild for one complete scheduled cycle.
7. In a separate contract release, remove old code and columns with paired provider upgrades.

## Tests After

1. Run the migration twice and compare rows, audit events, and progress records.
2. Interrupt each batch boundary and resume.
3. Run the canary search over database exports, logs, audit events, exception text, and API JSON.
4. Upgrade real-format SQLite and PostgreSQL copies through expand and contract.
5. Restore contract-before backups with their matching key rings and perform a read-only Git operation.

## Regression gates

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~LegacyGitCredentialMigrationServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter "FullyQualifiedName~RepositoryAnalyzerSourceTests|FullyQualifiedName~RepositorySourceSubmitTests|FullyQualifiedName~PrivateRepositoryVisibilityPropertyTests"
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~DbInitializer
rg "AuthAccount|AuthPassword" src --glob '*.cs' --glob '!**/Migrations/*'
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
```

The `rg` command must return no runtime matches after contract.

## Security and risk

- A revoked PAT cannot establish trusted external identity and must enter an Admin repair list.
- Backfill logs must use repository ID, connection ID, status, and safe error codes only.
- A contract rollback requires the matching pre-contract database and key ring.
- SQLite table rebuild is the highest data-loss risk and needs row-count, FK, index, and application smoke checks.

## Rollback

- Before contract, re-enable null-only legacy fallback and keep all backfilled connection data.
- Do not clear legacy fields during expand, backfill, or switch.
- After contract, restore the pre-contract database and matching key ring before deploying the older binary.
- Never bulk-decrypt protected PATs into legacy columns.

## Todo

- [x] Write migration, resolver, schema, and recovery failing tests.
- [x] Add explicit migration service and safe progress storage.
- [x] Stop legacy writes and deploy dual-read.
- [x] Backfill, resume-test, and verify through the new credential path (code and tests).
- [ ] Complete the switch gate on live data. Operator step: it needs the backfill against the real database and real providers, and it was not possible in the delivery session.
- [ ] Observe one full synchronization cycle. Operator step, after the backfill.
- [ ] Execute the separate contract release and recovery drill. Not done by design: the contract migrations (both providers), the `DbInitializer` contract method with the SQLite table rebuild, their copy tests, and the runbook are written, but nothing applies or enables them. The gates in the runbook cannot be met in one session.

## Success criteria

- [ ] Every migratable active remote repository uses a verified shared connection. Needs the live backfill.
- [x] Unmigratable repositories have explicit safe blocking records and prevent contract.
- [x] Migration is deterministic, idempotent, resumable, and secret-free.
- [ ] SQLite, PostgreSQL, and `DbInitializer` contract upgrades preserve all current data and constraints. Proven on copies only (test-covered); not applied to any live database.
- [ ] Runtime source and current snapshots contain no legacy credential fields after contract. Not done by design: the contract release is separate.
