---
title: English and Vietnamese wiki output
status: completed
priority: P1
effort: small
branch: main
tags: [wiki, configuration]
created: 2026-10-04
---

## Outcome

Stop the current `3q-game-client` develop job and configure wiki output for English (`en`) and Vietnamese (`vi`) only.
The current language list has twelve languages in Compose, system settings, and each branch.
Changing the translation setting alone does not remove stored branch languages.

## Constraints

Keep AI credentials and provider bindings intact.
Keep existing documents recoverable through soft deletion of their language records.
Keep the stopped job stopped when the API restarts.
The two existing queued jobs for `3q-game-battle` can resume with the new language list.
The API restarted before deployment, so its interrupted battle develop job must return to the queue after the configuration shutdown.
Do not add interface translation packs or a running-job cancellation feature.

## Work

- [x] Inspect the live job, language rows, translation worker, and configuration sources.
- [x] Stop the worker and verify that the current job ended during shutdown.
- [x] Change defaults, Compose values, local environment, and indexing language choices to `en,vi`.
- [x] Run focused language and connected-repository checks, frontend lint, and production builds.
- [x] Review the scoped changes and deployment data update.
- [x] Update stored branch languages and translation work after a database backup.
- [x] Start services and verify the stopped job, language lists, and new indexing choices.

## Acceptance

Each active branch has exactly `en` and `vi`, with English as the default.
No active translation task targets another language.
New indexing offers English and Vietnamese and defaults to English.
The stopped develop job does not restart.
The API and frontend are available after deployment.

## Verification

Spec compliance passed for the scoped defaults, English fallback, language selector, and transactional data update.
The data update passed its database invariant and live UI checks.
Focused backend checks passed: 100 passed, one PostgreSQL concurrency check skipped because its test database was not configured.
Focused frontend checks passed: 23 tests across three files.
Final repository frontend checks passed: 68 tests across nine files.
Frontend lint passed with no errors.
Both production Docker images built successfully.
Scoped code review passed, including a follow-up check of the public form, GitHub import, and quick-add entry points.
The language SQL passed a rollback rehearsal and committed its branch-language invariant.
All three active branches now have `en,vi`, and the client develop job remains cancelled after API startup.
No active translation task targets another language.
The battle develop job resumed with two languages, and its main job remains queued.
Browser verification shows `3q-game-client/develop` as Cancelled with `Languages: en, vi`.
The add-branches selector offers exactly English and Tiếng Việt, with English selected by default.
The selector was closed without submitting a new job.
The deployed API and frontend both returned HTTP 200.
No test runner or build process remains active.
