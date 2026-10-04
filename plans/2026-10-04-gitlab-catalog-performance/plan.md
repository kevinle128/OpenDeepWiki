# GitLab catalog performance

## Outcome

Reloading the repository workspace returns the first GitLab page without reading the full catalog.
Search, visibility, and sort use GitLab project queries.
Load more keeps the selected query and uses a protected provider page cursor.

## Scope

Keep connection authorization, token protection, DNS checks, and repository indexing intact.
Keep GitHub discovery unchanged.
Use GitLab search semantics and project-name sorting.
GitLab private filtering selects private projects; internal projects remain available with all access.
Do not change the database or delete current data.

## Work

- [x] Reproduce the delay in the live browser and confirm 9–12 second catalog responses in proxy logs.
- [x] Trace the full-page scan and the browser cancellation path.
- [x] Add native GitLab filtering and pagination with bound cursors.
- [x] Forward request cancellation through the web proxy.
- [x] Run focused regression tests and review the change.
- [x] Build the existing Compose services and measure the live workspace again.

## Acceptance

One catalog request makes one GitLab project request with the requested page size.
Search reaches remote projects beyond the first unfiltered page.
Cursor reuse with another connection or query fails before a provider request.
The live first page, search, and load more work without a full catalog scan.

## Verification

The GitConnections backend suite passed all 368 tests.
The frontend repository and API tests passed all 66 tests.
Full frontend lint passed.
Both production Docker images built successfully.
Independent code review approved the change without blocking findings.
The exact workspace URL produced a 10,500 ms catalog API response before deployment.
After deployment, the first catalog API response took 2,157 ms after startup and 1,072 ms on the next reload.
The browser displayed all 50 first-page repositories in 1,919 ms on the next reload.
Load more took 919 ms at the API and returned 100 unique rows with the first page intact.
Namespace search took 1,216 ms and found a repository absent from the first unfiltered page.
Live project-name sorting and private filtering passed; private filtering took 552 ms.
The browser console had no errors.
The database still contained one Git connection and one indexed repository after deployment.
No verification processes remain running.

## Status

Completed and deployed through `compose.pgsql.yaml`.
The initial verification missed GitLab's short-query exact matching; the [short-search follow-up](../261004-1721-gitlab-short-search/plan.md) repairs that regression and verifies `3q` in the live workspace.
