# Shared repository fixes

## Scope

Authenticated users can manage indexed branches of private repositories that use a shared Git connection.
Legacy private repositories still require owner or Admin access.
Repository deletion, visibility changes, and connection maintenance permissions are unchanged.

Catalog search, visibility filters, and sorting now run across all accessible provider pages before pagination.
Catalog cursors bind the connection and query parameters.
The scan rejects catalogs above 100 provider pages rather than returning incomplete search results.

The seven baseline backend failures and full frontend lint failures are fixed.
CI no longer excludes the failing backend tests or treats full lint as informational.
Review also found and repaired stale GitHub installation responses and message interpolation in that component.

No auto-sync toggle, legacy-data migration, or clone/fetch DNS change was added.
No production database or container was changed.

## Evidence

- Before the fix, browser tests reproduced private shared-user denial and missing search results from an unloaded provider page.
- Backend with disposable PostgreSQL: 1,389 passed, 1 skipped, 0 failed.
  The remaining test requires a copy of a real legacy database and is outside the requested scope.
- Backend solution build: passed, 0 warnings and 0 errors in the final incremental build.
- Browser suite with a production frontend build: 26 passed.
  It covers shared access, provider flows, branch add/sync/rebuild/retry/cancel/remove, responsive layouts, keyboard operation, stale responses, and secret evidence gates.
- Locale checks: all eight locales match the English baseline.
- Updated CI workflow: actionlint passed with an explicit file argument because this workspace has no Git metadata.
- Final frontend verification: 107 unit tests passed, full lint had no errors or warnings, TypeScript passed, and the production build passed.
- Independent re-review passed after the GitHub pagination and import response guards were repaired.
  Three regression tests cover obsolete pages, obsolete errors, and an import completion after A-to-B-to-A installation changes.

Existing compiler/analyzer warnings appeared during the test compilation.
The frontend build also reports the existing middleware convention deprecation and a parent lockfile notice.
These warnings did not fail the build; no warning suppression was added.

The browser provider is an in-process test provider.
These checks do not claim access to real GitHub or GitLab accounts.
The disposable PostgreSQL container was stopped and removed.
Ports 4310, 4311, and 54329 have no remaining listeners.

## Reports

- [Backend root causes](worker-261004-1255-backend-tests.md)
- [Repository components](worker-261004-1255-repo-lint.md)
- [Shared components](worker-261004-1300-component-lint.md)
- [Active plan](../261004-1250-shared-repository-fixes/plan.md)

The plan CLI rejects this single-file fix plan because it requires a phase file.
No extra phase file was added for this bounded repair.
