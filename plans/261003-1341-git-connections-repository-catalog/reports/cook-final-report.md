# Final delivery report: shared Git connections and repository catalog

Date: 2026-10-04. Mode: `--auto --advisor`. The workspace is not a git repository.

## Outcome

All six phases are delivered. Phase 4 delivers expand, backfill and switch; its contract step is authored but deliberately not applied. Several plan items are not delivered or need a decision (see below).

## Verified in this session

Each result below comes from a run I made myself after the last change.

- Backend: full suite with a disposable PostgreSQL 16 container, 1382 tests, 1374 passed, 1 skipped, 7 failed. The 7 failures are exactly the baseline set in `reports/baseline-failing-tests.txt`. `dotnet build OpenDeepWiki.sln` has 0 errors.
- Web: `tsc --noEmit` clean, Vitest 14 files and 98 tests, `check-i18n.js` passes for all eight locales, `npm run build` passes (run by the subagents).
- E2E: Playwright (Chromium) 26 of 26 tests pass, including the canary-token secret gate and the proxy proof.
- `data/opendeepwiki.db` is unchanged (3 Oct 18:30 timestamp) and the `opendeepwiki-*` containers were not touched.
- Independent review passes (read-only reviewers) after each phase; their findings were fixed or recorded in `plan.md` Delivery Deviations.
- After the last edits: `release.yml` parses as YAML, `ak plan validate` passes.

## Authored but unverified

- `quality.yml`, `docker-image.yml`, `release.yml`, and the Sealos template (syntax-checked only; CI cannot run here).
- Scenario rows 15 and 16 (database upgrade, container restart) rely on CI jobs that have never run. Both were rehearsed by hand on scratchpad copies and distinct compose projects.
- The successful `Migrated` path of the legacy credential runbook (needs a reachable provider).
- GitHub numeric-ID endpoints with a real token; any real provider, DNS or TLS behavior beyond loopback servers.
- `npm run test:e2e:ui`; a manual keyboard-only run.
- Linux container behavior of the key-ring volume permissions.

## Not delivered

- Per-repository auto-sync (no backend setting exists).
- Catalog "indexed" flag, connection activity feed, server-side catalog search, filter and sort.
- Legacy credential contract (columns still present).

## Decisions needing your confirmation (recommendation first)

1. RESOLVED. Creator on restore: the caller becomes the creator, and non-Admin readers see only audit events from the latest restore onward (`GitConnectionService.ListAuditEventsAsync`, filtered by the `Restored` event time). Admin still sees the full history.
2. Gitee token: Gitee calls in `GitPlatformService` no longer send a token; no setting for one is documented. Recommend: leave it unless you rely on private Gitee repositories.
3. Path uniqueness: `(OrgName, RepoName)` uniqueness was kept instead of replaced, with collision slugs. Recommend: accept.
4. RESOLVED (option A). A manual connect for a repository of another live connection returns 409 REPOSITORY_ALREADY_CONNECTED and changes nothing; only the legacy migration adopts existing repositories. Original item: Legacy adoption by clone URL and the widened adoption filter (another user's connect can overwrite a migrated repository's metadata). Recommend: restrict adoption to the migration path and require maintainer rights to relink.
5. Department rule: private repositories are visible to owner and Admin only. Recommend: add department access if your product uses it.
6. Auto-sync: needs a new column, worker filter, API and UI. Recommend: a separate small plan.
7. Catalog "indexed" flag and activity feed. Recommend: separate small plan.
8. `user: "0:0"` in `compose.pgsql.yaml` (added for key-ring write access). `compose.yaml` already had it per the ops report. Recommend: accept now, move to a non-root user with a chown init step later.
9. Backend cancel for sync and processing tasks (UI offers Cancel only for a pending full task). Recommend: keep.
10. Triage: `TranslationWorker` logged a `UNIQUE constraint failed` insert on `TranslationTasks` on an upgraded copy of the database. It is unrelated to this feature and was not investigated.

## Deployment notes

- The first deploy honours `JWT_SECRET_KEY` (previously ignored), so all sessions end once. Production now requires a JWT key of at least 32 UTF-8 bytes and `DataProtection__KeyRingPath`; fatal startup errors exit with code 1.
- Back up the database and the key ring together; losing the key ring makes stored tokens unreadable.
- Compare the older migration files with a backup or upstream copy: they all show the same modification time (3 Oct 21:57) and no baseline exists to prove they are unchanged.

## Other deviations

See `plan.md` Delivery Deviations. Highlights: hand-emptied `SyncModelSnapshot` migrations, a hand-merged `web/package-lock.json`, `docs/package.json` and `docs/bun.lock` fixed for a pre-existing docs build break, red-first TDD not confirmed for some test families.
