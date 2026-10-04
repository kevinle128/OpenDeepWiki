---
title: Shared repository fixes
description: Repair shared branch access, catalog discovery, and quality gates.
status: completed
priority: P1
created: 2026-10-04
---
# Shared repository fixes

## Outcome

Authenticated users can manage branches of private connected repositories in the shared workspace.
Catalog searches and filters reach repositories beyond the currently loaded provider pages.
The full backend suite and frontend lint pass without informational bypasses in CI.

## Constraints and non-goals

Keep connection maintenance, repository deletion, and visibility mutation permissions intact.
Preserve legacy private repository access where no shared connection exists.
Do not change auto-sync, DNS transport, migration, or live database data.
The workspace has no Git metadata, so commit history and diff inspection are unavailable.

## Work

- [x] Prove the private shared-user and unloaded-page search failures with E2E.
- [x] Fix shared branch access and add private and legacy access regressions.
- [x] Add server catalog filtering and sorting with bound pagination cursors and stale-request cancellation.
- [x] Fix the seven existing backend failures at their source.
- [x] Fix full frontend lint errors without rule suppression.
- [x] Remove CI exclusions after full tests and lint pass.
- [x] Verify focused tests, full tests, builds, E2E, and docs.
- [x] Re-review the GitHub installation-switch response guard and its regression test.

## Delivery

[Final verification report](../reports/fix-261004-1250-shared-repository-fixes.md).
Independent review passed after repairs to obsolete GitHub pagination and import responses.

## Acceptance

The private shared-user E2E and unloaded-page catalog E2E pass.
Legacy private repositories and destructive repository operations retain their existing access boundaries.
All available backend tests pass; required external PostgreSQL checks are reported separately.
Frontend unit tests, TypeScript, i18n, full lint, and production build pass.
Processes started for verification stop cleanly.
