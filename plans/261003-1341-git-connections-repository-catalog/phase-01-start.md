---
title: "Phase 1: Security and data foundation"
status: completed
phase: 1
plan: "git-connections-repository-catalog"
depends_on: []
scope: "backend, data, security"
test_strategy: "TDD with xUnit, SQLite integration tests, PostgreSQL migration tests, and secret canaries"
---

# Phase 1: Security and data foundation

## Context

Build the minimum shared data and secret boundary needed by all later phases.
Use one Shared Workspace.
All users in this plan are authenticated unless a test checks the 401 boundary.
Use [ADR 0001](../../docs/adr/0001-share-git-connections-in-one-workspace.md), [ADR 0002](../../docs/adr/0002-protect-shared-git-credentials.md), [identity research](./research/researcher-01-identity-security.md), and the [Phase 1 scout report](./reports/scout-phase-01-foundation.md).

## Requirements

- Add `GitConnection` with the global unique key `(Provider, NormalizedServerUrl, ExternalAccountId)`.
- Include soft-deleted rows in identity conflict handling and restore the existing connection instead of creating a duplicate.
- Add append-only `GitConnectionAuditEvent` records without tokens, ciphertext, headers, request bodies, or provider response bodies.
- Add nullable `Repository.GitConnectionId` with `Restrict` delete behavior.
- Use ASP.NET Core Data Protection with purpose `OpenDeepWiki.GitConnections.Pat.v1`.
- Persist the key ring outside the database, use one application name, and fail production startup when durable protected storage is absent.
- Fail production startup when the JWT signing key is absent, equals the current built-in default, or does not meet the documented strength rule.
- Do not reuse `AesConfigEncryption`.
- Centralize `CanUse` and `CanMaintain`.
`CanUse` requires an authenticated user and an enabled connection.
`CanMaintain` requires the creator or a current Admin resolved from the database, not only a JWT role claim.
- Store protected PAT payloads in an unbounded text column or a provider-safe column of at least 4096 characters, and verify maximum supported PAT size on SQLite and PostgreSQL.
- Do not change repository delete, visibility, or other dangerous repository permissions.
- Keep legacy credential fields for Phase 4 dual-read only.

## File inventory

| Path | Action | Rough size | Test impact |
|---|---|---:|---|
| `src/OpenDeepWiki.Entities/GitConnections/GitConnection.cs` | Create provider enum, identity, protected token, creator, status, validation fields. | M | Model and serialization tests. |
| `src/OpenDeepWiki.Entities/GitConnections/GitConnectionAuditEvent.cs` | Create append-only audit entity with safe codes and target IDs. | S | Audit redaction tests. |
| `src/OpenDeepWiki.Entities/Repositories/Repository.cs` | Add nullable connection FK and navigation only. | S | Existing repository fixtures compile unchanged. |
| `src/OpenDeepWiki.EFCore/MasterDbContext.cs` | Add `DbSet` values, indexes, lengths, `Restrict` relations, and provider-neutral filters. | M | SQLite model and constraint tests. |
| `src/OpenDeepWiki/Services/GitConnections/IGitConnectionSecretProtector.cs` | Create `Protect` and `Unprotect` contract. | S | Purpose and tamper tests. |
| `src/OpenDeepWiki/Services/GitConnections/DataProtectionGitConnectionSecretProtector.cs` | Implement Data Protection adapter. | S | Rotation and random payload tests. |
| `src/OpenDeepWiki/Services/GitConnections/IGitConnectionAuthorizationService.cs` | Create use and maintain decisions. | S | Permission matrix tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitConnectionAuthorizationService.cs` | Implement shared rules without repository-owner checks. | S | Creator/Admin/non-creator tests. |
| `src/OpenDeepWiki/Services/GitConnections/IGitCredentialResolver.cs` | Create short-scope credential resolution contract. | S | Analyzer seam tests. |
| `src/OpenDeepWiki/Services/GitConnections/GitCredentialResolver.cs` | Resolve enabled connection, unprotect PAT, and fail closed. | M | Disabled, deleted, corrupt payload tests. |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs` | Replace direct connection-path field reads with the resolver while legacy fallback stays centralized. | M | Remote refs, clone, and pull tests. |
| `src/OpenDeepWiki/Program.cs` | Register services and durable Data Protection configuration. | M | Startup configuration tests. |
| `src/OpenDeepWiki/appsettings.json`, `compose.yaml`, `compose.pgsql.yaml` | Add non-secret key-ring path and durable mounts. | S | Deployment smoke in Phase 6. |
| `src/OpenDeepWiki/Infrastructure/DbInitializer.cs` | Add idempotent SQLite and PostgreSQL expand DDL. | L | Existing-database upgrade tests. |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/*AddGitConnections*`, `src/EFCore/OpenDeepWiki.Postgresql/Migrations/*AddGitConnections*` | Generate paired migrations and designers. | M | Provider schema parity. |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/SqliteDbContextModelSnapshot.cs`, `src/EFCore/OpenDeepWiki.Postgresql/Migrations/PostgresqlDbContextModelSnapshot.cs` | Update generated snapshots through EF tooling. | M | Snapshot parity. |
| `tests/OpenDeepWiki.Tests/EFCore/GitConnectionModelTests.cs` | Create relational model and uniqueness tests. | M | New red tests. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitConnectionSecretProtectorTests.cs` | Create protection, tamper, rotation, and canary tests. | M | New red tests. |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitConnectionAuthorizationServiceTests.cs` | Create permission matrix tests. | S | New red tests. |
| `tests/OpenDeepWiki.Tests/Infrastructure/DbInitializerGitConnectionTests.cs` | Create new and upgraded SQLite/PostgreSQL schema tests. | L | Protect manual DDL path. |
| `tests/OpenDeepWiki.Tests/Chat/Config/TestConfigDbContext.cs`, `tests/OpenDeepWiki.Tests/Chat/Sessions/TestDbContext.cs` | Add required `IContext` sets. | S | Restore test compilation. |

Do not edit historical migrations, `CHANGELOG.md`, or generated files by hand.
Use EF tooling for new designer and snapshot output.

## Test scenario matrix

| Area | Red test | Expected result |
|---|---|---|
| Identity | Same provider/server/external ID, including a soft-deleted row | One row exists and the soft-deleted row is restored. |
| Authorization | Authenticated non-creator uses an enabled connection | Allowed. |
| Authorization | Non-creator maintains a connection | 403. |
| Authorization | Creator or current Admin maintains a connection | Allowed. |
| Secret | Protect the same PAT twice | Ciphertexts differ. |
| Secret | Modify one ciphertext byte | Unprotect fails closed. |
| Secret | Rotate Data Protection keys and restart | Old payload remains readable. |
| Startup | Production has no durable protected key ring | Startup fails with a safe message. |
| Startup | Production uses the built-in, missing, or weak JWT signing key | Startup fails before endpoints are mapped. |
| Disabled state | Resolve a disabled or deleted connection | Stable failure; no legacy fallback. |
| Redaction | Canary PAT passes through failure paths | No database, response, audit, or log match. |
| Schema | Create and upgrade SQLite and PostgreSQL databases | Equivalent tables, indexes, lengths, and `Restrict` relations. |
| Permissions | Relax branch use in later phases | Repository delete and visibility checks remain unchanged. |

## Function and interface checklist

- [x] `IGitConnectionSecretProtector.Protect` returns only protected text.
- [x] `IGitConnectionSecretProtector.Unprotect` fails on tampering or wrong purpose.
- [x] `IGitConnectionAuthorizationService.CanUse` checks authentication, deletion, and enablement.
- [x] `IGitConnectionAuthorizationService.CanMaintain` checks immutable creator ID or current Admin role.
- [x] `IGitCredentialResolver.ResolveAsync` never returns or logs protected storage values.
- [x] `RepositoryAnalyzer.GetRemoteBranchHeadCommitAsync` uses the resolver.
- [x] `RepositoryAnalyzer.PrepareWorkspaceAsync` uses the resolver for clone and pull.
- [x] `DbInitializer.InitializeAsync`, `MigrateSqliteAsync`, and `MigratePostgresqlAsync` stay idempotent.

## Dependency map

```text
JWT/IUserContext -> authorization service -> connection use or maintenance
IDataProtectionProvider -> secret protector -> credential resolver -> RepositoryAnalyzer
GitConnection -> Repository.GitConnectionId + audit events
MasterDbContext -> EF migrations + snapshots + DbInitializer DDL
```

## Tests Before

1. Add the model, authorization, protector, startup, and database-upgrade tests before production types exist.
2. Run each new test class and record that it fails for the expected missing behavior.
3. Add a canary PAT that appears only in test memory and assert zero matches in serialized output, captured logs, and persisted non-secret fields.

## Refactor and implementation

1. Add the entities and provider-neutral EF configuration.
2. Add the Data Protection adapter and durable production configuration.
3. Add centralized authorization and credential resolution.
4. Change only the analyzer credential seam needed for later phases.
5. Generate both provider migrations and update `DbInitializer` for existing databases.
6. Refactor duplication only when the red tests require one shared rule.

## Tests After

1. Run focused tests after each green step.
2. Run SQLite constraints with the SQLite provider, not EF InMemory.
3. Run PostgreSQL migration tests against an isolated disposable database.
4. Compare new-database EF schema with upgraded `DbInitializer` schema.
5. Re-run existing analyzer and repository permission tests.

## Regression gates

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter "FullyQualifiedName~GitConnectionModelTests|FullyQualifiedName~GitConnectionSecretProtectorTests|FullyQualifiedName~GitConnectionAuthorizationServiceTests|FullyQualifiedName~DbInitializerGitConnectionTests"
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter "FullyQualifiedName~RepositoryAnalyzerSourceTests|FullyQualifiedName~PrivateRepositoryVisibilityPropertyTests"
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
```

## Security and risk

- Persistent key-ring loss makes all PATs unreadable, so database and key-ring backups form one recovery unit.
- JWT roles can outlive role changes, so connection maintenance and migration administration must read current Admin state from the database.
- Validate concurrency behavior on SQLite and PostgreSQL instead of assuming `[Timestamp]` works the same way.
- A connection FK must never cause repository deletion.

## Rollback

- Roll back application code while leaving additive tables and nullable columns in place.
- Restore the paired database and key-ring backup if protected payload compatibility is lost.
- Do not decrypt PATs back into plaintext columns.

## Todo

- [x] Write all Phase 1 failing tests.
- [x] Add entities, mappings, constraints, and `IContext` updates.
- [x] Add Data Protection and durable deployment configuration.
- [x] Add authorization and resolver services.
- [x] Add paired migrations, snapshots, and `DbInitializer` DDL.
- [x] Pass focused, parity, regression, and build gates.

## Success criteria

- [x] The global connection identity is enforced on new and upgraded SQLite and PostgreSQL databases.
- [x] PATs use Data Protection and survive restart and key rotation.
- [x] Creator/Admin maintenance and shared authenticated use tests pass.
- [x] No secret crosses a response, audit, log, URL, queue, or persistence boundary in plaintext.
- [x] Legacy credentials remain only for the controlled Phase 4 transition.
