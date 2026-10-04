---
title: "Shared Git connections and repository catalog"
description: "Deep TDD delivery plan for secure shared Git connections, repository discovery, branch orchestration, legacy migration, and the Option C workspace."
status: delivered-with-open-decisions
priority: P1
effort: "XL"
tags: [git, security, repositories, migration, frontend, tdd]
created: 2026-10-03
---
# Shared Git connections and repository catalog

## Outcome

Scope correction on 2026-10-04: the user does not require per-repository auto-sync controls, legacy-data migration work, or additional clone/fetch DNS checks.
These items do not block the current delivery.
Follow-up fixes and their verification are tracked in [Shared repository fixes](../261004-1250-shared-repository-fixes/plan.md).

Deliver one Shared Workspace where every authenticated user can create and use Git connections and can add or manage indexed branches.
Only the connection creator or an Admin can maintain a connection.
Keep repository delete and visibility permissions unchanged.

## Goals

| # | Goal | Priority |
|---|---|---|
| 1 | Protect GitHub.com, GitLab.com, and HTTPS self-hosted GitLab PATs with ASP.NET Core Data Protection and a persistent key ring. | P1 |
| 2 | Discover provider repositories and branches through safe, paged APIs with stable identities. | P1 |
| 3 | Keep one Connected Repository per provider remote ID and run independent branch jobs. | P1 |
| 4 | Migrate legacy plaintext repository credentials with expand, backfill, switch, and contract releases. | P1 |
| 5 | Deliver the accessible Option C repository workspace and production E2E gates. | P1 |

## Non-goals

- Do not add tenants, organizations, OAuth, SSH, GitHub Enterprise, Gitee connections, or a second indexing pipeline.
- Do not reuse `AesConfigEncryption`, disable TLS validation, expose secrets, or break legacy GitHub App routes and UI contracts.
- Do not broaden repository deletion, visibility, or connection-maintenance permissions.
- Do not remove legacy credential columns while any unsupported Gitee or unknown-host repository still depends on them.

## Phases

| # | Phase | Status | Depends on |
|---|---|---|---|
| 1 | [Security and data foundation](./phase-01-start.md) | Done | None |
| 2 | [Provider catalog](./phase-02-provider-catalog.md) | Done | 1 |
| 3 | [Repository and branch orchestration](./phase-03-repository-branch-orchestration.md) | Done | 1, 2 |
| 4 | [Legacy credential migration](./phase-04-legacy-credential-migration.md) | Expand, backfill, switch done; contract authored, not applied | 1, 2, 3 |
| 5 | [Option C workspace UI](./phase-05-repository-workspace-ui.md) | In progress: UI delivered and unit tested; visual review and SKILL/auto-sync controls open | 2, 3 |
| 6 | [E2E, hardening, docs, and deployment](./phase-06-end-to-end-hardening.md) | Done (CI unverified) | 1-5 |

## Dependency graph

```text
Phase 1 -> Phase 2 -> Phase 3 -> Phase 4
                    \-> Phase 5 -> Phase 6
Phase 4 --------------------------^
```

## High-risk decisions

- Connection identity is globally unique by provider, normalized server URL, and provider external account ID, including soft-deleted rows.
- Repository identity is globally unique by provider, normalized server URL, and stable provider remote ID.
- Existing GitHub App imports and PAT imports converge through the same stable remote identity while their public routes remain compatible.
- Repository ownership remains the existing creator record; shared branch capability does not grant repository delete or visibility rights.
- Public HTTPS GitLab hosts work by default.
Private-network hosts and CIDRs require an operator allowlist with DNS and redirect revalidation.
- Disabling a connection preserves documentation but blocks discovery, new indexing, synchronization, and rebuild work.
- Removing a branch deletes only its local index and returns 409 while a branch job is processing.
- Migration contract starts only after verified SQLite and PostgreSQL backups, persistent key-ring backup, backfill proof, and one full update cycle.

## Overall success criteria

- [ ] Every implementation phase starts with a failing test and ends with focused and regression gates.
- [ ] No PAT or protected payload appears in API JSON, logs, audit events, URLs, browser storage, screenshots, traces, or queue messages.
- [ ] SQLite, PostgreSQL, and `DbInitializer` create equivalent schemas and pass upgrade tests.
- [ ] All authenticated users can use connections and manage branches, while only creator/Admin maintain connections.
- [ ] The same remote is registered once, branch jobs are independent, and active branch removal returns 409.
- [ ] Legacy plaintext fields are removed only after expand/backfill/switch/contract gates and joint database/key-ring recovery proof.
- [ ] Unit, integration, E2E, accessibility, responsive, build, docs, and deployment gates pass.

## Evidence

- [Research](./research/) and [scout reports](./reports/)
- [Final delivery report](./reports/cook-final-report.md)
- [ADR 0001](../../docs/adr/0001-share-git-connections-in-one-workspace.md)
- [ADR 0002](../../docs/adr/0002-protect-shared-git-credentials.md)
- [Option C mockup](../../mockups/designs/git-account-flow-option-c/index.html)

## Red Team Review

### Session — 2026-10-03

**Findings:** 15 accepted after deduplication.
**Severity breakdown:** 3 Critical, 9 High, 3 Medium.

The review covered security, failure recovery, load-bearing assumptions, and scope control.
All accepted findings were applied to the owning phases.
See the [adjudication report](./reports/red-team-review.md).

## Validation Log

### Session 1 — 2026-10-03

The user confirmed the product decisions before plan creation.
The confirmed decisions include one Shared Workspace, shared connection use, creator or current Admin maintenance, PAT authentication, multi-branch indexing, independent branch jobs, protected credentials, safe legacy migration, and Option C UI.
No unresolved product decision remains.

### Verification Results

- **Tier:** Full
- **Claims checked:** 90
- **Verified:** 90 | **Failed:** 0 | **Unverified:** 0
- The four red-team verification roles checked at least 15 plan claims per phase against source evidence.
- Accepted failures in the first draft were corrected before this final verification result.

### Whole-Plan Consistency Sweep

- Files reread: `plan.md` and all six `phase-*.md` files.
- Decision deltas checked: 15.
- Reconciled stale references: 7.
- Unresolved contradictions: 0.
<!-- slug: git-connections-repository-catalog -->

## Delivery Deviations

- Phase 2: red-first TDD was not confirmed separately for the URL validator, cursor codec, and two provider clients.
- Phase 2: link-local and metadata addresses are always blocked, even if allowlisted (stricter than planned).
- Phase 2: a rejected provider token returns 422 instead of 401, because the web client signs the user out on any 401.
- Phase 2: restore of a soft-deleted connection makes the caller the creator. Resolved: the new creator owns the connection but sees audit events only from the latest `Restored` event onward (no schema change). Admin keeps the full history.
- Phase 2: Gitee calls in `GitPlatformService` no longer send a token (it was in the query string). No Gitee token setting is documented anywhere; no header form was verified.
- Phase 1: `SyncModelSnapshot` migrations have hand-emptied `Up` and `Down`.
- Phase 3: kept the `(OrgName, RepoName)` uniqueness rule that the phase said to replace. Workspace paths, regeneration and `/{org}/{repo}` routes depend on it. Slug collisions get a free slug (`acme~gitlab`, then numbers). Awaiting user decision.
- Phase 3: a legacy repository with the same normalized clone URL is adopted by the connection and keeps its legacy credential fields. This overlaps Phase 4 relinking. RESOLVED (option A): adoption of an existing repository by clone URL belongs to the legacy migration only; a manual connect for a repository of another live connection fails with REPOSITORY_ALREADY_CONNECTED (409) and changes nothing.
- Phase 3: connected repositories start as `Completed`. Not red-first for analyzer origin tests, worker lease tests, and the legacy-route HTTP test.
- Phase 3: no route or job calls `CleanupRemovedWorkspacesAsync` yet; clone and fetch cannot pin DNS answers.
- Phase 3 review fixes: reads and mutations on private repositories are hidden from users who are not the owner or an Admin (no department rule). Cleanup of removed workspaces now runs from the branch generation worker every 5 minutes.
- Phase 3: `RepositoryOrchestrationModelTests.Sqlite_RemoteIdentity_IgnoresSoftDeletedRepositories` failed once in a full run and could not be reproduced.
- Phase 4: the contract step is authored and tested on copies but not applied or enabled. Its gates (backups, backfill proof, one update cycle, no Gitee or unknown-host dependency) cannot be met in this session.
- Phase 4: until Phase 5, the web form breaks for private repository submits because legacy credential fields now return 400.
- Phase 4: Gitee, self-hosted GitLab and unknown-host repositories are blocked from backfill and block the contract.
- Phase 4 review fixes: GitUrl userinfo is redacted in list and admin DTOs; creator selection waits for earlier failed repositories; adoption order is deterministic. The widened adoption filter still lets another user's connect overwrite a migrated repository's metadata (RESOLVED: a manual connect for a repository of another live connection now returns REPOSITORY_ALREADY_CONNECTED before any change).
- Intermittent test failures: two full runs each had one extra `ObjectDisposedException` failure from SQLite in different tests; not reproduced alone.
- Phase 5: the catalog API is cursor-paged only, so search, access filter and sort run on the repositories loaded so far (labeled in the UI). The page number is not in the URL.
- Phase 5: SKILL generation and auto-sync controls are not offered. The connect and add-branch requests have no such fields and no backend setting exists for auto-sync. Open decision: add backend fields, or accept the gap.
- Phase 5: no catalog-wide "indexed" state or filter. The catalog items carry no local repository ID. The index state of one repository is resolved on selection through the repository list (connection ID plus clone URL).
- Phase 5: red-first order was confirmed for the API layer and for the connection list, catalog, plan, managed and workspace components. The connection dialog, submit form picker and page tests were written after their code.
- Phase 5: auto-sync per repository is not delivered. No per-repository auto-sync setting exists in the backend (only the global incremental update worker and an unused `Repository.UpdateIntervalMinutes`). This is an unmet plan item that needs a user decision.
- Phase 5: SKILL generation is offered only for a repository that is not yet indexed; the catalog "indexed" flag, connection activity feed, and server-side search/filter/sort are not delivered (client-side over loaded pages).
- Phase 5: browser visual, keyboard and accessibility checks are deferred to Phase 6. Some UI tests were written after their code.
- Phase 6: CI workflows, the Sealos template, and the container smoke on Linux are authored but unverified. `compose.pgsql.yaml` runs as `user: "0:0"` for key-ring write access.
- Phase 6: Cancel in the managed branches panel is offered only for a pending full task, matching the backend.
- Phase 6: `web/package-lock.json` was hand-merged (three Playwright entries) because the local npm rewrote unrelated entries.
- Phase 6: `TranslationWorker` logged a `UNIQUE constraint failed` insert on `TranslationTasks` on an upgraded copy of the database; unrelated to this feature, not investigated.
