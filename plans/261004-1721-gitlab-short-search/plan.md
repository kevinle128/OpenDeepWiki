# GitLab short search

## Outcome

The repository workspace finds partial names and namespaces for one- and two-character searches such as `3q`.
Keep provider paging and do not read the full catalog before returning matching rows.
Show a filter-specific message when the search has no results.

## Evidence

The live workspace returns zero rows for `3q` and eight rows for `3q-`.
GitLab's [SQL pattern implementation](https://gitlab.com/gitlab-org/gitlab/-/raw/master/lib/gitlab/sql/pattern.rb) uses exact matching when the query has fewer than three characters.
The Projects API does not expose a parameter to disable that limit.
The current UI uses the empty-connection message for every zero-row response.

## Scope

For short searches, read sorted provider pages without the native search parameter and filter each page locally.
Return the first page with matches, keep its next-page cursor, and skip empty pages.
Keep native search for queries with at least three Unicode characters.
Keep connection authorization, token protection, DNS checks, and GitHub behavior intact.
Do not change database data or retry branch generation jobs.

## Work

- [x] Reproduce the failure in the live workspace and verify the provider cause.
- [x] Add short-search pagination and regression checks for matches on later pages.
- [x] Repair and test the filter-specific empty state.
- [x] Review, build Compose services, and verify `3q` on the live workspace.

## Acceptance

`3q` finds the eight live repositories and includes the selected repository.
Short-query cursors retain their query, sort, visibility, page size, and connection binding.
An empty provider page does not hide matches from a later page.
The first matching page returns without a complete catalog scan.
Normal first-page loading remains fast.

## Verification

The focused provider suite passed all 52 tests.
The GitConnections regression suite passed all 373 tests.
The frontend repository and API tests passed all 69 tests.
Full frontend lint and both production Docker builds passed.
Independent review approved the short-query loop, cursor handling, and empty state.
After deployment, `3q` returned eight repositories, including the selected `3q-game-client`.
The first short-query API request took 1,930 ms after startup; the next reload took 918 ms at the API and 1,639 ms to display the list in the browser.
Load more took 2,363 ms, preserved eight unique rows, and reached the end of the catalog.
A query with no matches displayed `No repository matches the filters.` instead of the empty-connection message.
The browser console had no errors.
All verification processes exited.

## Status

Completed and deployed through `compose.pgsql.yaml`.
