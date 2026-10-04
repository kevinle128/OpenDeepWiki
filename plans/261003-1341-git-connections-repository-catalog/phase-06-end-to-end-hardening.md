---
title: "Phase 6: End-to-end hardening, docs, and deployment"
status: completed
phase: 6
plan: "git-connections-repository-catalog"
depends_on: [1, 2, 3, 4, 5]
scope: "Playwright E2E, CI, observability, docs, deployment"
test_strategy: "User-level E2E first, then full backend, frontend, docs, migration, and container gates"
---

# Phase 6: End-to-end hardening, docs, and deployment

## Context

Verify the completed flow in a real browser and production-like runtime.
Use the [Phase 6 scout report](./reports/scout-phase-06-e2e-hardening.md), both accepted ADRs, and all earlier phase criteria.
This phase adds verification and closes operational gaps.
It does not add product scope.

### Execution constraints for this delivery

- The workspace is not a git repository. `.github/workflows/*.yml` can be authored but not executed or validated here; report them as unverified and do not simulate CI.
- The running `opendeepwiki-*` containers are off-limits. Check `lsof` for free fixed ports, use a temporary SQLite database in the scratchpad, and use a distinct compose project name and ports for any container smoke test. Never run `docker compose up` on the default project name. Never touch `data/opendeepwiki.db`.
- Docs describe what shipped. The source of truth is `plan.md` Delivery Deviations. Traps: the legacy credential contract is authored but not applied (link the existing runbook `docs/content/docs/deployment/legacy-credential-migration.mdx` instead of re-describing it); there is no per-repository auto-sync setting; the Gitee token was removed and not replaced; there is no department rule for private repositories; a rejected provider token returns 422, not 401; the JWT key needs at least 32 UTF-8 bytes and `DataProtection__KeyRingPath` is required in Production.
- No product scope. Auto-sync, the catalog indexed flag, the activity feed, server-side catalog search/filter/sort, and the open decisions in `plan.md` are user decisions, not work for this phase.
- The secret-absence E2E (canary PAT through DOM, every response body, URL, localStorage, sessionStorage, console, and Playwright trace files) is a blocking gate, not one spec among several.
- Rehearse the contract runbook only on a scratchpad copy of `data/opendeepwiki.db` with synthetic legacy rows added.
- Playwright Chromium download may be slow or blocked. Report a failure instead of working around it.
- Carried from Phase 5 review (web-only, small): keyboard behavior needs real-browser checks; resizing across 950px remounts the workspace; on narrow screens the workspace always opens on the Connections tab even when the URL names a repository; the repository lookup in `web/lib/git-connections-api.ts` checks only the first 50 keyword matches; one German string in `repositories.json` equals English.

## Requirements

- Add Playwright with Chromium only and deterministic frontend and API ports.
- Use a local ASP.NET Core test host, temporary SQLite database, and in-process fake provider.
- Set `API_PROXY_URL` explicitly to the fixed test API origin before Next.js starts, and prove every proxied request reaches that host.
- Keep DNS, redirect, address-policy, and TLS tests at provider integration level with an injectable resolver and a local HTTPS server; Playwright covers only the user-visible result.
- Do not call real providers or store real PATs in default E2E.
- Cover the complete GitHub, GitLab.com, and self-hosted GitLab user flows.
- Cover anonymous, authenticated non-creator, creator, and Admin behavior.
- Cover secret absence, provider failures, stale UI state, responsive layouts, and keyboard-only use.
- Run SQLite and PostgreSQL migration and container smoke tests.
- Persist the Data Protection key ring in every deployment definition.
- Put quality gates before image publication.
- Update only user, API, architecture, development, and deployment docs affected by the implemented behavior.
- Do not modify `CHANGELOG.md`.

## File inventory

| Path | Action | Rough size | Test impact |
|---|---|---:|---|
| `web/playwright.config.ts` | Create deterministic server lifecycle, Chromium project, retry, trace, and screenshot rules. | M | E2E runner. |
| `web/e2e/repository-workspace.spec.ts` | Create GitHub, GitLab, self-hosted, catalog, plan, and branch flows. | L | Four or more E2E tests. |
| `web/e2e/repository-workspace-permissions.spec.ts` | Create anonymous, shared use, creator, Admin, and disabled tests. | L | Four E2E tests. |
| `web/e2e/repository-workspace-responsive.spec.ts` | Create 1440px, 900px, 390px, keyboard, and overflow checks. | M | Two or more E2E tests. |
| `web/e2e/repository-workspace-secrets.spec.ts` | Check DOM, responses, URL, storage, console, trace policy, and errors for canary secret. | M | Secret E2E. |
| `tests/OpenDeepWiki.Tests/Integration/GitConnectionWorkflowTests.cs` | Add complete SQLite backend workflow if earlier phases did not create it. | L | Integration gate. |
| `web/package.json`, generated `web/package-lock.json` | Add `@playwright/test`, `test:e2e`, and `test:e2e:ui`. | S | Install and CI. |
| `Makefile` | Add `test-e2e` without slowing the default quick test. | S | Developer workflow. |
| `.github/workflows/quality.yml` | Add backend, frontend, i18n, E2E, docs, migration, and Docker smoke jobs. | L | Required CI. |
| `.github/workflows/docker-image.yml`, `.github/workflows/release.yml` | Require quality for the same commit or tag before publish. | M | Release safety. |
| `README.md` | Document shared workspace and test commands. | S | Link and command checks. |
| `docs/content/docs/getting-started/local-development.mdx` | Document browser install and E2E commands. | S | Docs build. |
| `docs/content/docs/api-reference/repositories.mdx` | Document connection, catalog, branch, errors, and legacy removal. | M | Contract review. |
| `docs/content/docs/architecture/frontend.mdx` | Document route, components, state, and E2E layout. | S | Docs build. |
| `docs/content/docs/architecture/backend.mdx` | Document provider, protection, authorization, and orchestration. | M | Docs build. |
| `docs/content/docs/architecture/data-models.mdx` | Document connections, stable repository identity, branches, jobs, and audit. | M | Docs build. |
| `docs/content/docs/configuration/environment-variables.mdx` | Document implemented key-ring and allowlist configuration only. | M | Deployment review. |
| `docs/content/docs/deployment/docker-compose.mdx`, `docs/content/docs/getting-started/docker-deployment.mdx` | Document persistence, backup, upgrade, and smoke checks. | M | Operator review. |
| `compose.yaml`, `compose.pgsql.yaml`, `scripts/sealos/sealos-template.yaml` | Align key-ring volume, health checks, and required runtime options. | M | Container smoke. |

Generated lockfiles are updated by their package manager, not by hand.

## Test scenario matrix

| # | E2E scenario | Expected result |
|---:|---|---|
| 1 | Authenticated user creates GitHub connection, pages catalog, selects branches, and submits plan | Branch tasks start independently. |
| 2 | Same flow with GitLab.com | Same behavior and safe errors. |
| 3 | Same flow with public HTTPS self-hosted GitLab | Works without an operator allowlist. |
| 4 | Same flow with allowlisted private-network GitLab | Works only under operator policy. |
| 5 | User adds, syncs, rebuilds, retries, cancels, and removes a managed branch | Other branches remain unchanged. |
| 6 | Anonymous user opens workspace or calls APIs | Redirect or 401. |
| 7 | Authenticated non-creator uses connection and manages branches | Allowed; connection maintenance absent and API returns 403 if forced. |
| 8 | Creator and Admin maintain connection | Allowed. |
| 9 | Disabled connection with existing docs | Docs remain readable; discovery and new work return clear disabled state. |
| 10 | Remove a processing branch | 409 and branch remains. |
| 11 | Canary token completes create and error flows | No token in DOM, response, URL, storage, console, audit, log, screenshot, or retained trace. |
| 12 | Provider returns timeout, 401, 429, or 5xx while user changes selection | Stable error and no stale response overwrite. |
| 13 | 1440px, 900px, and 390px viewport checks | Correct columns/tabs, no critical loss or horizontal overflow. |
| 14 | Keyboard-only main flow | Focus order, activation, dialog return, live status, and alert focus work. |
| 15 | SQLite and PostgreSQL upgrade from legacy data | Expand/backfill/switch/contract and recovery gates pass. |
| 16 | Default and PostgreSQL containers restart | Connections remain decryptable and health checks pass. |

## Function and interface checklist

- [ ] Playwright owns and stops every process that it starts.
- [ ] Fixed ports are checked before startup and stale project-owned processes are handled explicitly.
- [ ] Fake provider behavior exists only in the test dependency-injection boundary.
- [ ] Failed E2E retains restricted trace and screenshot artifacts; successful secret-flow artifacts are not retained.
- [ ] Structured logs include connection ID, provider, action, result, and duration, but no secret or provider body.
- [ ] `/health` remains available.
- [ ] Publish jobs depend on quality for the exact commit or tag.
- [ ] Docs state actual implemented names, routes, config keys, commands, and migration steps.

## Dependency map

```text
Backend unit/integration + frontend unit/i18n/lint/build
  -> Playwright user flows
  -> SQLite/PostgreSQL migration and recovery smoke
  -> docs build + Docker build/runtime smoke
  -> image publish
```

## Tests Before

1. Add the E2E harness and first failing user journey before any hardening changes.
2. Reproduce each permission, disabled, stale-response, secret, responsive, and keyboard boundary in Playwright.
3. Add failing container restart and key-ring persistence smoke checks.
4. Add CI dependency tests or workflow validation before changing publish workflows.

## Refactor and implementation

1. Build the deterministic test host and Playwright lifecycle.
2. Add the 16 scenarios in small stable groups.
3. Fix discovered product defects in their owning earlier-phase services or components, with focused regression tests.
4. Add safe structured observability and artifact handling.
5. Add quality workflow and publication dependencies.
6. Align deployment volumes, health checks, and startup order.
7. Update the smallest owning documentation surfaces after behavior is final.

## Tests After

1. Run each E2E file alone, then the full suite three consecutive times.
2. Inspect failure artifacts for secret exposure before enabling CI retention.
3. Inspect 390px, 900px, and 1440px screenshots for pixel defects.
4. Run keyboard-only flow manually once in addition to automation.
5. Run paired database recovery and container restart smoke tests.
6. Verify every changed docs link and command against the final source and scripts.

## Regression gates

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
cd web && npm ci && npm test
cd web && node scripts/check-i18n.js
cd web && npm run lint && npm run build
cd web && npx playwright install --with-deps chromium
cd web && npm run test:e2e
cd docs && npm ci && npm run lint && npm run build
docker compose build
docker compose up -d
curl --fail http://localhost:18081/health
curl --fail http://localhost:8090/
docker compose ps
docker compose logs --no-color opendeepwiki web
docker compose down
```

Repeat the migration and runtime smoke with `compose.pgsql.yaml`, using its actual API port `8080` and web port `3000`, or align the compose port contracts before implementation.

## Security and risk

- Restrict failure artifacts because repository names can be sensitive even when tokens are redacted.
- Clear token fields immediately after submit and disable trace capture while a raw token is visible if masking cannot be proved.
- Do not put tokens in query strings because the frontend proxy logs URLs.
- Stop only project-owned processes and always terminate those started by the test harness.
- Cache NuGet and npm dependencies, but never cache database files, PATs, key rings, or test secrets.

## Rollback

- Revert publication dependency changes only together with an equivalent quality gate.
- Keep persistent key-ring volumes during application rollback.
- Stop and remove only processes and temporary databases created by the E2E harness.
- Use the Phase 4 paired database/key-ring restore process for contract rollback.

## Todo

- [x] Write the deterministic E2E harness and all scenario tests.
- [x] Pass the suite three consecutive times and inspect visual and secret artifacts.
- [x] Add integration, migration, recovery, and container restart smoke tests. (Backend workflow test added; SQLite and PostgreSQL upgrade, runbook rehearsal, and both container stacks run once by hand. See `reports/cook-phase-06-ops-report.md`.)
- [x] Add quality-before-publish CI. (Authored and linted with actionlint only; not executed, UNVERIFIED.)
- [x] Update affected product, API, architecture, development, and deployment docs.
- [ ] Pass every regression gate on SQLite and PostgreSQL.

## Success criteria

- [ ] All E2E scenarios pass three consecutive times with no orphan process.
- [ ] Secret searches have zero matches across runtime and browser artifacts.
- [ ] Backend, frontend, i18n, lint, builds, docs, migrations, and both container stacks pass.
- [ ] Visual checks at all three viewports and keyboard-only checks pass.
- [ ] Production key-ring persistence, backup, restore, and restart behavior is documented and proved.
- [ ] No image publishes before quality succeeds for the same revision.

## Carried from Phase 1

- Data Protection key files are plain XML guarded by file permissions. Evaluate certificate or KMS protection of the key ring.
- First deploy honours `JWT_SECRET_KEY` (previously ignored), so all sessions are invalidated once. Document this.
- Fatal startup errors now exit with code 1. Document this.
- Compose files now require `JWT_SECRET_KEY` and set `DataProtection__KeyRingPath`. Update `README.md` and `.env.example`.
- The `SyncModelSnapshot` migrations have hand-emptied `Up` and `Down` bodies because earlier hand-written migrations left snapshots stale.
- Investigate intermittent test failures: one SQLite `ObjectDisposedException` appeared in different tests across two full runs, and `RepositoryOrchestrationModelTests.Sqlite_RemoteIdentity_IgnoresSoftDeletedRepositories` failed once. Check shared SQLite and HTTP test host disposal under parallel xUnit execution.
- Rehearse the legacy credential contract runbook end to end and exercise the GitHub numeric-ID endpoints with a real token.
