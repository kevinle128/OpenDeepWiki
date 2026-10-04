---
title: "Phase 5: Option C repository workspace UI"
status: completed
note: "Workspace, localization, secret handling, and the private-repository submit form fix are delivered and unit tested. Pixel review at 1440/900/390px, keyboard-only flows in a browser, and SKILL/auto-sync controls remain open. See reports/cook-phase-05-report.md."
phase: 5
plan: "git-connections-repository-catalog"
depends_on: [2, 3]
scope: "Next.js workspace, accessibility, responsive UI, localization"
test_strategy: "TDD with Vitest, Testing Library, API contract tests, and visual review"
---

# Phase 5: Option C repository workspace UI

## Context

Implement the authenticated Option C workspace from the [mockup](../../mockups/designs/git-account-flow-option-c/index.html).
Use [UI/API research](./research/researcher-03-ui-api-tests.md) and the [Phase 5 scout report](./reports/scout-phase-05-ui.md).
Desktop uses connection, repository catalog, and plan columns.
Widths below 950px use three accessible tabs.

### Inherited from earlier phases

- Priority one: the legacy credential fields (`authAccount`, `authPassword`) now return a stable 400 on submit and admin update. The private-repository submit form is broken until it sends `gitConnectionId` and drops the account and password fields.
- `GitUrl` and `SourceLocation` in repository DTOs are now userinfo-redacted.
- Status codes to map: 422 for a rejected provider token (not 401, because the web client signs the user out on 401), 409 for an active branch job and for deleting a connection that repositories use, 404 for a repository the caller cannot see.
- Read these instead of guessing the contracts: `src/OpenDeepWiki/Endpoints/GitConnectionEndpoints.cs`, `ConnectedRepositoryEndpoints.cs`, `BranchGenerationEndpoints.cs`, `IncrementalUpdateEndpoints.cs`, `Admin/AdminGitConnectionMigrationEndpoints.cs`, `web/lib/repository-api.ts`, and the Models DTOs.
- Capabilities such as `canMaintain` come from backend responses only, never from frontend roles.
- Private repositories are hidden from non-owners who are not Admin (no department rule).
- Open user decisions stay untouched: creator-on-restore, Gitee token, path uniqueness, legacy adoption, department rule.
- Runtime: do not rebuild or restart the running `opendeepwiki-*` containers. `web/node_modules` is installed. Visual, E2E and accessibility gates belong to the final phase.

## Requirements

- Add `/repositories` under the authenticated main app layout, not the Admin layout.
- Show all shared connections to authenticated users.
- Show maintenance actions only when backend `canMaintain` is true.
- Never infer capabilities from frontend roles.
- Never display, refill, cache, persist, or put secrets in a URL.
- Use server pagination, filtering, search, and stable catalog identity.
- Ignore or cancel stale catalog results after connection or filter changes.
- Support branch selection, language, SKILL generation, auto-sync, task progress, add, sync, rebuild, retry, cancel, and remove.
- Show branch-level results and errors.
- Preserve existing GitHub import pages and repository API types.
- Use all eight locales and existing i18n validation.

## File inventory

| Path | Action | Rough size | Test impact |
|---|---|---:|---|
| `web/app/(main)/repositories/page.tsx` | Create authenticated page and URL selection coordination. | M | Page tests. |
| `web/components/repositories/repository-workspace.tsx` | Create desktop columns and narrow-screen tabs. | L | Layout and state tests. |
| `web/components/repositories/connection-list.tsx` | Create semantic connection selection and capability actions. | M | Keyboard and permission tests. |
| `web/components/repositories/connection-dialog.tsx` | Create provider-specific form with empty secret on edit. | L | Focus, validation, and secret tests. |
| `web/components/repositories/repository-catalog.tsx` | Create paged search, filters, sorting, selection, and states. | L | Request race and UI tests. |
| `web/components/repositories/index-plan-panel.tsx` | Create multi-branch plan controls and per-branch results. | L | Payload and partial-result tests. |
| `web/components/repositories/managed-branches-panel.tsx` | Create task controls, active-job removal guard, and status. | L | Action and live-region tests. |
| `web/lib/git-connections-api.ts` | Add typed calls through `apiClient`. | M | URL, payload, and error tests. |
| `web/types/git-connection.ts` | Add provider, connection, capability, catalog, and branch DTOs. | M | Type/build gate. |
| `web/app/sidebar.tsx` | Add authenticated repository workspace navigation. | S | Navigation test. |
| `web/i18n/request.ts`, `web/hooks/use-translations.ts`, `web/types/i18n.d.ts` | Register `repositories` namespace. | M | i18n gate. |
| `web/i18n/messages/{de,en,es,fr,ja,ko,pt-BR,zh}/repositories.json` | Create complete workspace messages. | L | Key parity. |
| Eight `sidebar.json` files | Add navigation label. | S | Key parity. |
| `web/components/data-table-shell.tsx`, `web/components/table-pagination.tsx`, `web/components/status-badge.tsx` | Move only reused generic UI and keep compatibility exports. | M | Existing admin tests. |
| `web/components/repo/branch-generation-status.tsx` | Localize status and add polite live updates. | M | Existing component tests. |
| Six or more workspace/API test files from the scout inventory | Create at least 24 behavior tests. | L | New red tests. |

Do not create a global state library.
Use page state, URL search parameters, and existing React hooks.

## Test scenario matrix

| Area | Red test | Expected result |
|---|---|---|
| Route | Anonymous opens `/repositories` | Redirect to `/auth`. |
| Layout | Width is at least 950px | Three columns remain visible and usable. |
| Layout | Width is 900px or 390px | Radix tabs preserve all three steps and critical status. |
| Selection | Keyboard selects connection and repository | Enter and Space work; semantic selected state is announced. |
| Capability | Non-maintainer views a shared connection | Can use it; cannot see maintenance controls. |
| Secret | Edit connection opens | Token field is empty and only `hasSecret` is shown. |
| Secret | Submit and error flows complete | Token is absent from DOM, URL, storage, cache, toast, and captured request logs. |
| Catalog race | Slow old request finishes after new selection | Old result does not replace current catalog. |
| Catalog paging | Search, filter, sort, and page change | Correct encoded request and predictable page reset. |
| Disabled | Connection is disabled | Existing docs remain reachable; discovery and branch actions explain why they are unavailable. |
| Plan | Multiple branches are selected | Request preserves language, SKILL, auto-sync, and branch names. |
| Results | Backend returns created, existing, and failed branches | Each branch result is visible; no false single success toast. |
| Active removal | Backend returns 409 | UI preserves branch and gives a focused error. |
| Accessibility | Dialog closes | Focus returns to trigger; error summary is focusable. |
| Localization | All locale files load | Keys match English baseline and no hard-coded status remains. |

## Function and interface checklist

- [ ] `git-connections-api.ts` uses `web/lib/api-client.ts` for auth and `ApiError` behavior.
- [ ] `RepositoryWorkspace` owns selected IDs and narrow-screen tab state.
- [ ] Child components receive DTOs and callbacks instead of fetching duplicate catalogs.
- [ ] Catalog requests carry cancellation or a current request identity.
- [ ] `ConnectionDialog` sends a token only when the user entered a replacement.
- [ ] `IndexPlanPanel` shows each branch result.
- [ ] `ManagedBranchesPanel` reuses current generation task methods and response types.
- [ ] Existing `RepositoryItemResponse`, GitHub import public props, and branch task contracts remain compatible.

## Dependency map

```text
/repositories page -> AppLayout -> RepositoryWorkspace
RepositoryWorkspace -> ConnectionList -> ConnectionDialog
RepositoryWorkspace -> RepositoryCatalog -> IndexPlanPanel or ManagedBranchesPanel
All workspace components -> git-connections-api -> apiClient
Status and tables -> existing UI primitives + compatible generic exports
```

## Tests Before

1. Create API tests for query encoding, payload shape, auth reuse, 401/403/409, and secret absence.
2. Create component tests for layout, tabs, keyboard input, capabilities, catalog races, dialogs, and branch results.
3. Add responsive match-media fixtures for 1440px, 900px, and 390px.
4. Confirm the focused tests fail before components exist.

## Refactor and implementation

1. Add types and the API client layer.
2. Add page and workspace state with URL parameters for non-secret selection and filters.
3. Add connection list and dialog.
4. Add server-paged catalog with stale-response protection.
5. Add index plan and managed branch panels by reusing existing task APIs.
6. Extract only the generic admin table, pagination, and badge code that the workspace needs, with compatibility exports.
7. Add localization and accessibility states.

## Tests After

1. Run each component test during implementation.
2. Run the i18n key checker after every locale change.
3. Run lint and production build.
4. Inspect 1440px, 900px, and 390px layouts for overflow, clipped controls, lost status, incorrect focus, and visual drift from Option C.
5. Check keyboard-only create, select, plan, and remove flows.

## Regression gates

```bash
cd web && npm test -- git-connections-api repository-workspace connection-list connection-dialog repository-catalog index-plan-panel managed-branches-panel
cd web && node scripts/check-i18n.js
cd web && npm run lint
cd web && npm run build
```

## Security and risk

- Store only connection ID, repository ID, filters, sorting, and page in the URL.
- Do not put tokens or provider error bodies in React Query caches, browser storage, toast text, analytics, or console output.
- Use backend capability fields as the only UI authority.
- A hidden action is not authorization; backend tests remain required.
- Do not reuse the credential-bearing part of `RepositorySubmitForm`.

## Rollback

- Remove the sidebar entry or feature exposure while keeping backend APIs and existing GitHub import routes.
- Keep compatibility exports if generic components move.
- Do not delete user data or connections to roll back the UI.

## Todo

- [x] Write API, workspace, accessibility, race, and responsive failing tests.
- [x] Add types, API client, page, and workspace shell.
- [x] Add connection, catalog, plan, and managed branch components.
- [x] Add all locale files and navigation text.
- [ ] Run focused tests, i18n, lint, build, keyboard checks, and pixel review. (Focused tests, i18n, lint, and build are done. Browser keyboard checks and pixel review are open.)

## Success criteria

- [ ] Authenticated users complete connection-to-index and branch-management workflows from `/repositories`.
- [x] Capability-driven connection maintenance and shared branch use match backend policy.
- [x] No secret appears or persists in the browser surface.
- [ ] The 1440px, 900px, and 390px layouts are accessible, complete, and visually correct.
- [x] Existing GitHub import and repository task interfaces remain compatible.
