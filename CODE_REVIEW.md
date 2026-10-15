# 1.0.0 code review and quality notes

## Scope

Source reviewed against the **0.2.5 FLOATING_ART_FIX** archive, not the older experimental 0.3.x branch. The goal was to simplify the final distribution without altering the validated behavior.

## Removed components

- `AllScopeAudit.cs` — diagnostic queue and logging, retaining `Classify()` only.
- `CandidateAudit.cs` — diagnostic queue and logging, retaining `IsMainClothingCell()` only.
- `SelectionProbe.cs` — experimental trace and item-stat sampling.
- `FilterPanelUi.cs` — redundant pass-through to the actual dropdown.
- `StatReader.Sample()` and an unused helper; all positive-skill lookups remain.
- Unused navigation helpers, stale match counters, visual logging, font enumeration logs, per-recache stopwatch, unused mask-construction flags and obsolete diagnostic configuration.

## Performance changes

- Reusable `FilterTargets`, `SeenTargetIds`, and `StaleMaskIds` replace temporary per-scan collections.
- `_normalizedSelected` is computed once per filter selection rather than once per checked item.
- Removed logging-only work inside UI scans, and dropped unused matches/unknown counters.
- Preserved the already-tested 6-frame UI update, 36-frame local rescan, and deferred scroll layout refresh; avoided speculative changes to these timings.
- Preserved controller caching and the localized `GetComponentsInChildren<ItemCell>` search. Rare global searches remain only as **discovery fallbacks** when a local controller/cell root cannot be located.

## Safety checks

- No Harmony patch registration was added.
- Filtering touches existing UI components only (`CanvasGroup`, `LayoutElement`, and `ScrollRect` layout refresh).
- The game's own `m_currentItemCell` is **read** for selection verification; the only programmatic selection action remains `ItemCell.Select()` through native Unity UI focus. The plugin does not directly call item add/remove, equipment, or save methods.
- Native **Recently Added** cells remain in place; deduplication is not present.
- Unsupported tabs, insufficient/implausible cell snapshots, and missing required UI guards fail open.
- `NullableAttributes.cs` was examined for removal and verified to be **necessary** for compilation with the provided IL2CPP interop references. It remains.

## Verification limits

- Built in Release for **net6.0**, against the game/BepInEx IL2CPP interop reference assemblies.
- Static review, source-invariant checks, and archive integrity checks do **not** substitute for a live game test.
- At least one in-game run of this cleaned binary should confirm that NO FILTER, ALL / Clothing / Tools, auto-selection, right-side details, tooltip avoidance, newly acquired items and save reload are unchanged.
