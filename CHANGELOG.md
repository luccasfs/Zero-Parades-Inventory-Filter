# Changelog

## 1.0.0 — Clean release (2026-10-15)

**Based on the gameplay-validated v0.2.5 behavior.**

- Removed developer-only candidate audits, ALL-scope audits, item-stat dumps, manual-selection trace diagnostics, and unused config bindings.
- Preserved only the scope-classification helpers needed by the production filter.
- Preserved UI-selection verification and warnings: these are safety mechanisms, not optional diagnostics.
- Removed the unused UI delegation wrapper and unused selection navigation helpers.
- Simplified dropdown refresh to accept the only value it actually uses (selected skill).
- Reused temporary target, key, and stale-mask collections to avoid per-refresh allocations.
- Normalized the selected skill once per filter change for cached stat comparisons.
- Removed per-refresh status counters and debug logging from the release code path.
- Removed obsolete recache timing and font-catalog logs.
- Preserved the 6-frame filter-update cadence and 36-frame scoped ItemCell recache introduced during the validated responsive-filter work.
- Preserved deferred scroll rebuilding and the floating-artwork safeguard.
- Renamed plugin-owned UI GameObjects from the obsolete `ZP_CF_` prefix to `ZP_IF_`.
- Updated the plugin and assembly to 1.0.0; documented installation, configuration, build, safety and rollback in English.
- Kept the old auto-selection configuration **key** for user settings compatibility.

**No changes were intended to:** skill detection, eligible inventory scopes, drug handling, automatic selection rules, artwork masking, tooltip hover bounds, or UI coordinates.

### Baseline history

- **0.2.1** — Native scrolling refresh after restoring filtered cells.
- **0.2.2** — Inventory Filter naming and Tools tab support, rebased on 0.2.1.
- **0.2.4** — Reverted experimental deduplication that broke NO FILTER.
- **0.2.5** — Suppressed floating selected-item art; gameplay-confirmed working by tester.
