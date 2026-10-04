# Phase 5 cook report: Option C repository workspace UI

Status: DONE_WITH_CONCERNS. The workspace, the localization, the secret handling and the private-repository submit form fix are delivered and unit tested. Browser-level checks (pixel review at 1440/900/390px, keyboard-only flows) and the SKILL/auto-sync controls are not done. The reasons are below.

## Gates

- `npx tsc --noEmit`: clean.
- `npx vitest --run`: 14 files, 84 tests, all pass (baseline was 4 files, 18 tests). New: 10 files, 66 tests.
- `node scripts/check-i18n.js`: all eight locales match English.
- `npm run build`: passes; `/repositories` is a route.
- ESLint on every changed or new file: 0 errors, 0 warnings. `repository-submit-form.tsx` had two `set-state-in-effect` errors before this work; they are fixed (see decisions).
- Backend untouched, so the backend suite was not rerun. No docker container was rebuilt or restarted.

## Priority one: private-repository submit form

`web/components/repo/repository-submit-form.tsx` no longer has account or password fields or state. For a private Git repository it renders `private-connection-picker.tsx`, which lists enabled connections of the same server as the typed URL (`isSameGitServer`), blocks submit without a choice, and sends `gitConnectionId`. The request never carries `authAccount` or `authPassword`. Connection error codes (disabled, not found, host mismatch, legacy fields, URL with credentials) show localized text instead of backend text. The old `home.repository.auth*` keys are replaced by eight `connection*` keys in all eight `home.json` files. The two optional fields stay in the `RepositorySubmitRequest` type for compatibility.

## Delivered

- Types and API: `web/types/git-connection.ts`, `web/lib/git-connections-api.ts` (through `apiClient`; `getErrorCode`, `getErrorBranches`, abortable catalog calls, `findConnectedRepository`).
- Page and URL state: `web/app/(main)/repositories/page.tsx` and `workspace-url-state.ts`. Anonymous visitors go to `/auth`. The URL holds only connection ID, provider repository ID, search, access and sort.
- Components in `web/components/repositories/`: workspace (three columns from 950px, three Radix tabs below it, a selection strip above the tabs), connection list, connection dialog, catalog, index plan panel, managed branches panel, `error-key.ts`, `language-options.ts`.
- Navigation: `app/sidebar.tsx` entry (authenticated only), `repositories` namespace in `i18n/request.ts`, `hooks/use-translations.ts`, `types/i18n.d.ts`, all eight `repositories.json` and `sidebar.json` files.
- `branch-generation-status.tsx`: localized, polite live region, exported `GenerationStatusBadge` (icon plus text). Its test now uses real English messages.
- Hook `hooks/use-narrow-layout.ts` (`matchMedia` at 949px; the 768px `useIsMobile` is not used).

## Status code mapping

422 (rejected provider token) shows a localized "enter a valid token" message and does not sign the user out (only 401 does, through the shared client). 409 maps by error code: active branch job (branch stays, focused alert), connection in use, disabled, conflict. 404 shows "does not exist or you cannot see it". 429, 504 and 5xx have their own text. Backend message text is never shown; the stable code is the contract.

## Secrets

The edit dialog opens with an empty token field and only says that a token is saved (`hasSecret`). The update call omits the `token` key unless a replacement was typed. The token field is cleared after success and after failure. Tests check the DOM, local storage, session storage and the URL after a 422. Nothing is cached or put in a URL.

## Deviations and gaps (decisions needed)

1. **SKILL generation and auto-sync are not offered.** The connect and add-branch requests have no such fields, and no backend setting for auto-sync exists. Showing toggles would be fake controls, so the plan panel says they are not available yet. The matrix row "request preserves language, SKILL, auto-sync" is therefore covered for language and branch names only. Options: add backend fields (a small additive change to `ConnectRepositoryRequest`, `AddIndexedBranchesRequest` and a sync setting), or accept the gap.
2. **Search, access filter and sort run on the loaded page set.** `GET /git-connections/{id}/repositories` accepts only `cursor` and `pageSize`. The catalog uses the cursor with a "load more" button, filters the loaded items on the client, and says so in the UI. The page number is not in the URL (cursors are opaque). Stale answers are ignored through an epoch counter and aborted through `AbortController` when the connection changes.
3. **No catalog-wide "indexed" state or filter.** Catalog items carry no local repository ID. On selection, the workspace finds the local repository through `/api/v1/repositories/list?keyword=` (connection ID plus normalized clone URL, first 50 matches) and shows the managed panel or the index plan. A repository whose name has more than 50 keyword matches may not be found. A backend lookup by connection and provider repository ID would remove this.
4. **Inventory deviations (files outside the phase list):** `components/repo/repository-submit-form.tsx`, `components/repo/private-connection-picker.tsx` and the eight `home.json` files (required by the inherited priority one); `types/repository.ts` (optional `gitConnectionId` and `hasGitConnection` on `RepositoryItemResponse`, `gitConnectionId` on the submit request); `hooks/use-narrow-layout.ts`, `components/repositories/{error-key,language-options,test-fixtures}.ts`, `test-utils/render-with-intl.tsx`, `app/(main)/repositories/workspace-url-state.ts`.
5. **Not done from the inventory:** the generic extraction of `data-table-shell`, `table-pagination` and `status-badge` (the catalog is cursor-paged and uses neither the table nor numbered pages, so the move would add churn with no reuse); `connection-activity-feed.tsx` (in the scout list, not in the phase inventory; the audit events API exists and is not shown).
6. **Test order.** Red first was confirmed for the API layer, connection list, catalog, plan panel, managed panel and workspace. The connection dialog, the submit form tests, the page and sidebar tests and the generation status test were written after their code. The dialog and submit form behavior is covered by tests now.
7. **Disabled connection:** discovery and branch actions are blocked with an explanation, and a link to the existing documentation area (`/private`) is shown. The link is a general entry, not a per-repository deep link, because disabled connections do not list repositories.
8. **Focus return:** the dialogs have no trigger element, so the opener is passed as a ref and focus is restored in `onCloseAutoFocus`.
9. **Keyboard activation** of connection and repository items relies on native `button` elements and Radix checkbox and tabs. jsdom does not synthesize Enter or Space clicks, so the tests check the semantics (native button, `aria-current`, text selected mark) and click behavior, not key events.

## Verified against the backend

- A GitHub connection stores `https://github.com` as its server URL (verified by `GitConnectionService.cs:67`), and `GitRemoteOriginGuard.IsSameOrigin` (`GitRemoteOriginGuard.cs:53-66`) compares scheme, host and port only. The picker uses the same rule (`isSameGitServer`), with a test. SSH URLs (`git@...`) match no connection, which is consistent with SSH being a non-goal.
- `REPOSITORY_ALREADY_CONNECTED` (a repository that already uses another connection) is thrown at `ConnectedRepositoryService.cs:156` but has no case in the endpoint `Describe` switch, so the response is HTTP 500 with the stable code in the body. The UI maps the code (`errors.alreadyConnected`). Backend gap, not changed: it should be 409.
- The plan is not tracked in the local plan store (`ak plan list` is empty), so phase status was set in the phase file and `plan.md`; `ak plan validate` passes. This report is written in the plan `reports/` folder as the task asked, not in the hook's `plans/reports` path.

## Open for the final phase

Pixel and overflow review at 1440, 900 and 390px, browser keyboard-only create/select/plan/remove flow, automated accessibility scan, and an end-to-end run against a real backend. Item 3 above (indexed lookup) and item 1 (SKILL and auto-sync) need a decision before then.

## Unresolved questions

- Add backend fields for SKILL generation and auto-sync, or accept that the workspace does not offer them?
- Add a backend lookup of a connected repository by connection ID and provider repository ID (and a per-connection "indexed" flag in the catalog), or keep the client-side join?
- Show the connection audit events in the workspace (activity feed), or leave it out of this release?

## Gap closure

Status: DONE_WITH_CONCERNS. Gaps 1 and 3 are closed. Gap 2 is closed for SKILL generation. Auto-sync is left out on purpose (finding below).

### REPOSITORY_ALREADY_CONNECTED returned 500

`ConnectedRepositoryEndpoints.Describe` had no case for it. It now maps to 409. A table test reads every constant of `ConnectedRepositoryErrorCodes` and `IndexedBranchRemovalErrorCodes` and fails if one maps to 500 (red before the fix). A second test does the same for every `GitConnectionErrorCodes` constant through `GitConnectionEndpoints.Describe`; it passed at once, so those codes were already mapped. The provider codes thrown by `GitConnectionService` (`ServerUrlInvalid`, `UnsupportedProvider`) were already mapped. `Describe` is now `internal` in both endpoint classes so the tests can call it. Note: `REPOSITORY_ALREADY_CONNECTED` is thrown only by `AdoptLegacyRepositoryAsync`, which the legacy migration calls. No connect or add-branch route reaches it today, so the 409 matters for any future route and for the contract.

### SKILL generation

`ConnectRepositoryRequest` has a new last parameter `bool GenerateSkill = true`. A request without the field keeps the legacy default. `CreateRepositoryAsync` stores it on the new `Repository`. A repository that already exists (found by identity, adopted, or attached) keeps its setting, the same as legacy submit, which ignores the field for an existing repository. `AddIndexedBranchesRequest` has no field because its repository always exists, so the field would do nothing. No schema change: `Repository.GenerateSkill` and its DDL already exist. Tests: default true, false stored, existing repository keeps its value (service level), and the JSON field through HTTP.

### Auto-sync finding (nothing added)

- `IncrementalUpdateWorker` runs a global schedule. `IncrementalUpdate:Enabled` (appsettings) switches it for all repositories. It scans every non-deleted repository with status Completed. `DefaultUpdateIntervalMinutes`, `MinUpdateIntervalMinutes`, `MaxRepositoriesPerPoll` are global too.
- The only per-repository field is `Repository.UpdateIntervalMinutes` (null means the global default) with `LastUpdateCheckAt`. It is an interval, not an on/off setting. No endpoint, service, or admin model writes or reads it (searched `src`).
- Manual sync exists (`IncrementalUpdateEndpoints`, `TriggerManualUpdateAsync`) and the workspace already offers it.
- So there is no per-repository auto-sync switch anywhere. Connected repositories are already covered by the global schedule when it is enabled. A per-repository toggle would need a new column and a worker filter, which is a new mechanism, so it was not added. The workspace shows no auto-sync control. Decision for the user: add a per-repository switch (new column, worker filter, API, UI) or accept the global schedule.

### Web

The plan panel shows a "Generate a SKILL.md package" checkbox (default on) only when the repository is not indexed yet, and sends `generateSkill`. When branches are added to an indexed repository the toggle is hidden because the backend ignores it there. The old "not offered yet" notice (`repositories.plan.notice`) is replaced by `generateSkill` and `generateSkillHelp` in all eight `repositories.json` files. Vitest tests were written first (two failed as expected), then the code.

### Gates

- Backend full suite with Postgres 16 (disposable container on port 55439, removed): 1371 passed, 1 skipped, 7 failed. The 7 are exactly the ones in `baseline-failing-tests.txt`.
- `dotnet build OpenDeepWiki.sln`: 0 warnings, 0 errors.
- Web: `npx tsc --noEmit` clean; `npm run build` passes; ESLint on changed files clean; `npx vitest --run` 14 files, 86 tests pass; `node scripts/check-i18n.js` passes.
- Files touched: `ConnectedRepositoryEndpoints.cs`, `GitConnectionEndpoints.cs` (visibility only), `ConnectedRepositoryModels.cs`, `ConnectedRepositoryService.cs`, `ConnectedRepositoryEndpointsTests.cs`, `ConnectedRepositoryServiceTests.cs`, `web/components/repositories/index-plan-panel.tsx` and its test, `web/types/git-connection.ts`, eight `repositories.json`.

## Review fixes

- Submit form: every ApiError now shows the localized `repositories.errors.*` text from `errorKey`. Backend message text is never shown. Other errors show the generic submit text.
- Submit form: the failure log now writes only the stable error code, not the error object.
- Index plan panel: `loadMore` uses an AbortController that aborts on unmount, and a late result or failure sets no state.
- Public Git submit: the request omits the `gitConnectionId` key (before, it was present as undefined). A regression test checks no connection ID, account, or password.
- Tests added: 4 (3 in repository-submit-form.test.tsx, 1 in index-plan-panel.test.tsx), each confirmed red before the fix.
