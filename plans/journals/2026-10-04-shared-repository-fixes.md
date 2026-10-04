---
title: Shared repository fixes
date: 2026-10-04
summary: "Fix shared private branch access, full catalog search, baseline tests and lint."
---

# Shared repository fixes

## What happened

E2E reproduced denied private branch access for a shared user and a missing repository on an unloaded catalog page.
The shared access helper and server catalog query now cover both cases.
Baseline backend source-path and document-index defects were repaired.
Frontend lint fixes preserve loading, controlled state, and request cleanup.

## Decision

Keep repository deletion and visibility permissions unchanged.
Do not add auto-sync controls, migration work, or clone/fetch DNS changes.
Use isolated test data and leave production containers unchanged.

## Next steps

Final E2E passed all 26 checks, frontend passed all 107 unit tests, and independent review passed.
The source changes are ready; production Docker containers still use the previous build.
AgentWiki publish skipped.

> Historical work record — not durable authority. Prefer docs/specs/ADRs for current decisions.
