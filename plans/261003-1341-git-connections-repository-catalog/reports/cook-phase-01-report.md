# Phase 1 cook report: security and data foundation

Status: DONE_WITH_CONCERNS. All Phase 1 requirements are implemented and the four regression gates pass. The only failing tests are the 7 baseline failures.

## Results

- Gate 1 (four new classes): 57 passed, 0 failed, 0 skipped. This includes the PostgreSQL tests, run against a disposable `postgres:16-alpine` container on port 55432. The container was removed afterwards.
- Gate 2 (`RepositoryAnalyzerSourceTests`, `PrivateRepositoryVisibilityPropertyTests`): 31 passed, 6 failed. The 6 failures are the baseline `RepositoryAnalyzerSourceTests` failures.
- Gate 3 (full suite): 695 passed, 7 failed, 702 total. The 7 failures are exactly the baseline list (6 `RepositoryAnalyzerSourceTests`, 1 `RepositorySkillMarkdownBuilderTests`). Before this phase the suite had fewer tests; no new failures.
- Gate 4 (`dotnet build OpenDeepWiki.sln`): 0 warnings, 0 errors.
- Manual startup check with the built app: Production without a key ring exits 1 with a safe message. Production with the default JWT key exits 1. Production with a strong key and `DataProtection__KeyRingPath` starts and `/health` returns 200; the key file is written. Development with no configuration starts.

## What changed

- Entities: `GitConnection`, `GitConnectionAuditEvent`, `Repository.GitConnectionId` and navigation. `IContext` and `MasterDbContext` get two `DbSet`s.
- Model: the identity index `(Provider, NormalizedServerUrl, ExternalAccountId)` is unique with no `IsDeleted` filter, so a soft-deleted row blocks a duplicate and `GitConnection.Restore()` re-enables it. All three relations use `Restrict`. `ProtectedToken` has no length limit (`TEXT` on SQLite, `text` on PostgreSQL). `MasterDbContext` rejects updates and deletes of audit events.
- Services in `src/OpenDeepWiki/Services/GitConnections/`: Data Protection protector (purpose `OpenDeepWiki.GitConnections.Pat.v1`), authorization service, credential resolver. `RepositoryAnalyzer` now takes `IGitCredentialResolver` for remote refs, clone and pull.
- `Program.cs`: one Data Protection application name (`OpenDeepWiki`), key ring persisted to `DataProtection:KeyRingPath`, services registered, and Production-only fail-fast through `Program.ValidateProductionSecurity`. Startup failure now sets exit code 1. This is intentional and applies to every fatal startup error, not only the new checks; the old code exited 0 after `Log.Fatal`.
- `DbInitializer`: idempotent SQLite and PostgreSQL expand DDL (tables, indexes, nullable `Repositories.GitConnectionId` with a `Restrict` foreign key).
- Migrations: generated with the local `dotnet-ef` 10.0.8 tool for both providers.
- Config: `appsettings.json` has an empty `DataProtection:KeyRingPath`. `compose.yaml` and `compose.pgsql.yaml` set the key-ring path under the existing `/data` mount.

## Decisions and deviations the orchestrator must know

1. Migration drift. The model snapshots were stale: five hand-written migrations never updated them. A single generated migration would have contained about 750 lines of unrelated changes. I generated an empty `SyncModelSnapshot` migration per provider first, then `AddGitConnections`, so `AddGitConnections` is pure tool output. The only hand edit is that the `Up` and `Down` bodies of `SyncModelSnapshot` are empty, with a comment. Designers and snapshots are untouched tool output. `has-pending-model-changes` reports clean for both providers.
2. The migration chain cannot run from an empty database. The existing `AddAiModelCachePricing` migration expects `AiModelConfigs`, which the initial migration does not create. This was true before this phase and does not affect runtime, which uses `EnsureCreated` and `DbInitializer`. The migration tests therefore build a legacy-shaped database, mark earlier migrations as applied, and run only `AddGitConnections`.
3. Index name. The default unique-index name is 64 characters, over the PostgreSQL limit of 63. The index is named `IX_GitConnections_Identity` through the `Relational:Name` annotation (the EFCore project has no relational package reference).
4. `JWT_SECRET_KEY` was ignored in the old code, because `appsettings.json` always supplied a value. `Program.ResolveJwtSecretKey` now uses `Jwt:SecretKey` unless it is the built-in default, then `JWT_SECRET_KEY`, then the built-in default (Development only). Both JWT call sites use it.
   Upgrade impact: every deployment that sets `JWT_SECRET_KEY` (the documented way, and what `.env` and `compose.yaml` do) switches signing keys on the first deploy of this change. All existing user sessions become invalid and users must log in again. This is one-time and loses no data.
5. JWT strength rule (none was documented): at least 32 UTF-8 bytes and not the built-in default.
6. "Durable protected storage" is interpreted as a required explicit `DataProtection:KeyRingPath` in Production. I did not add certificate protection of the key files, so keys are plain XML protected by file permissions and the volume. Open question below.
7. `compose.yaml` and `compose.pgsql.yaml` now require `JWT_SECRET_KEY` (`${JWT_SECRET_KEY:?...}`) because both run in Production and used the built-in key. `.env` and `.env.example` already define it. `README.md` and `.env.example` were not edited (outside file ownership).
8. `CanMaintainAsync` also requires the caller to exist as a non-deleted user, so a still-valid token of a deleted account cannot maintain a connection. Admin is read from `UserRoles` and `Roles` (`!IsDeleted`, role `IsActive`), not from the JWT.
9. `RepositoryAnalyzer` has one constructor with the resolver. I edited the single constructor call in `RepositoryAnalyzerSourceTests.cs` (a compile fix, file not in the inventory) with a no-credential stub.
10. EF Core nulls the foreign key of tracked repositories when a tracked connection is removed, even with `Restrict`. Only untracked dependents are protected by the database foreign key. Phase 2 and later code must soft-delete connections and never physically remove one in a context that tracks its repositories. Tests cover the database behavior with an untracked context.
11. Concurrency. `Version` is a nullable `[Timestamp]` column that neither provider fills, so two concurrent updates both succeed and the last write wins on SQLite and on PostgreSQL (tests document this). Phase 2 rotation and disable need an explicit concurrency token to return 409.
12. SQLite upgrade path: the foreign key to `GitConnections` is added with the new nullable column, and it matches a new database exactly (verified by the schema parity test).

## Files

New source: `src/OpenDeepWiki.Entities/GitConnections/{GitConnection,GitConnectionAuditEvent}.cs`; `src/OpenDeepWiki/Services/GitConnections/{IGitConnectionSecretProtector,DataProtectionGitConnectionSecretProtector,IGitConnectionAuthorizationService,GitConnectionAuthorizationService,IGitCredentialResolver,GitCredentialResolver}.cs`; migrations `*_SyncModelSnapshot` and `*_AddGitConnections` (with designers) for Sqlite and Postgresql; `dotnet-tools.json` at the repo root (local `dotnet-ef` 10.0.8).

Modified source: `Repository.cs`, `MasterDbContext.cs`, `RepositoryAnalyzer.cs`, `Program.cs`, `DbInitializer.cs`, `appsettings.json`, `compose.yaml`, `compose.pgsql.yaml`, both model snapshots.

Tests, new: `EFCore/GitConnectionModelTests.cs`, `Services/GitConnections/{GitConnectionSecretProtectorTests,GitConnectionAuthorizationServiceTests,GitCredentialResolverTests}.cs`, `Infrastructure/{DbInitializerGitConnectionTests,ProductionStartupSecurityTests}.cs`, `Services/Repositories/RepositoryAnalyzerCredentialSeamTests.cs`, `TestPaths.cs`. Tests, modified: both `IContext` test doubles and `RepositoryAnalyzerSourceTests.cs` (one constructor call).

PostgreSQL tests run only when `OPENDEEPWIKI_TEST_POSTGRES` is set (a connection string to a disposable server); otherwise they show as skipped. Temporary files use `OPENDEEPWIKI_TEST_SCRATCH` or the OS temp directory.

## Files outside the phase inventory

`RepositoryAnalyzerSourceTests.cs` (edited, one constructor call), `TestPaths.cs`, `GitCredentialResolverTests.cs`, `ProductionStartupSecurityTests.cs`, `RepositoryAnalyzerCredentialSeamTests.cs`, `dotnet-tools.json`, and the two `*_SyncModelSnapshot` migrations with designers.

`GitConnection.Restore()` clears the soft-delete flags only. It does not re-enable a disabled connection; that policy belongs to Phase 2. A test locks this.

The Success criteria boxes are ticked. Only the audit, log, and persistence boundaries are verifiable in Phase 1. The response, URL, and queue boundaries are covered when those surfaces appear in later phases.

## Scope not done (by design)

No connection API, identity service, or provider validation (Phase 2). No change to repository delete, visibility, or branch permissions. Legacy `AuthAccount` and `AuthPassword` stay and are read only inside `GitCredentialResolver` when `GitConnectionId` is null.

## Unresolved questions

- Should Production also require a certificate to encrypt the key-ring files? The current rule only requires a persistent key-ring directory.
- Should `README.md` and `.env.example` document `DataProtection__KeyRingPath` and the required `JWT_SECRET_KEY` (Phase 6 docs)?
