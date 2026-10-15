# Zero Parades Inventory Filter v1.0.0 — Independent Implementation Audit

**Date:** 15 October 2026  
**Purpose:** Technical code-and-packaging similarity assessment before independent publication.  
**Assessment type:** Static binary metadata/IL comparison, source review, and release-archive inspection. **Not a legal opinion or a clean-room certification.**

## Executive finding

**No evidence of direct reuse of the original plugin's compiled implementation was found in the reviewed Inventory Filter v1.0.0 package.** The assemblies have substantially different structures and use notably different UI construction and item-stat extraction techniques. The only significant unresolved issue is **visual-design similarity**: comments in our openly distributed source describe measuring dropdown geometry from a reference GIF and matching aspects of the original's appearance. An IL comparison cannot determine whether the resulting presentation is sufficiently distinct.

**Publication recommendation:** Reasonable to present as a separately implemented, independently maintained alternative, with explicit inspiration credit and no original DLL/assets bundled. Before publishing, review the side-by-side appearance and consider changing unusually distinctive decorative choices if they closely reproduce the original. Do not describe this as a formally clean-room implementation; the original binary and behavior were inspected during development. No technical audit provides a guarantee against copyright claims or platform moderation.

## Evidence examined

| Evidence | Original plugin | Independent plugin |
|---|---|---|
| Assembly | `ParadesClothingFilter.dll` | `ZeroParadesInventoryFilter.dll` |
| SHA-256 | `8b704d2881fbff3ec0c95a713a483d6adbef176bb8ca58b717562503b89360d1` | `c2e5a6817985bbbc641e4e280eabec409306e9d63835b5c39aa8f9d3dd72de59` |
| Size | 114,688 bytes | 41,472 bytes |
| Types, including generated/compiler types | 22 | 12 |
| Declared methods, including constructors and generated methods | 287 | 90 |
| Declared assembly references | 11 | 14 |
| Embedded managed resources | 0 | 0 |
| Target framework indicated by assembly metadata | .NET Framework 4.7.2 | .NET 6 |

Reviewed release archive: `ZeroParades_InventoryFilter_v1.0.0.zip` (SHA-256 `ba4b6e7504e83cbc972447108e6dfdf4eb8f22be502cd94a0bf8eb8221b3dc72`). Archive integrity check passed. It contains the independent plugin DLL, source, BepInEx configuration, README, changelog, build log, and code-review notes. It does **not** include `ParadesClothingFilter.dll` or third-party art/image files.

## Methodology and results

1. **Managed assembly metadata:** Used Mono.Cecil to enumerate types, methods, field types, referenced assemblies, IL operations, method calls, literal strings, and embedded resources from both DLLs.
2. **Method-level similarity:** No methods containing at least 25 IL instructions had *identical opcode sequences*. No methods containing at least 20 IL instructions had *identical normalized opcode-and-operand sequences*. The closest general-purpose opcode-pattern comparisons were between unrelated methods and are not a reliable measure of copyright similarity. Different compilers/framework targets also limit mechanical comparisons.
3. **Method identifiers:** The overlapping method names were `.ctor`, `.cctor`, `Load`, `Update`, and `HasPositiveStat`. The first four are standard framework/lifecycle names; `HasPositiveStat` is a short descriptive name for the same gameplay problem but has different underlying implementations.
4. **Literal strings:** Found **24 unique exact common IL string literals**, among 658 unique strings in the original and 130 in the v1.0.0 build. These were largely game skill labels (`Sensors`, `Shadowplay`, `Blueprints`, etc.), game inventory object identifiers (`InventoryGroup`, `InventoryContentGroup`), generic terms/punctuation, and the functional dropdown caption `-- NO FILTER --`. No substantial original diagnostic text, proprietary text passage, or distinctive long explanatory string was duplicated.
5. **Specific UI-construction difference:** The original `TryCreateDropdown` calls `UnityEngine.Object.Instantiate<TMPro.TMP_Dropdown>` on a discovered dropdown template. The v1.0.0 `InventoryDropdownUi.Create`/`BuildPopup` constructs its own Unity UI hierarchy (GameObjects, canvas, graphics, scroll region, text and click targets). These approaches are materially different.
6. **Specific stats-extraction difference:** The original has a broad `ItemStatsExtractor` with reflective type/member resolution; the v1.0.0 `StatReader` reads typed IL2CPP item/gear modifier APIs. This supports independent implementation, despite naturally shared gameplay terminology and skill names.
7. **Patching/inventory operations:** The original assembly references `0Harmony` and contains patch-installation methods; the independent assembly has no assembly-level `0Harmony` reference and the reviewed source does not register Harmony patches. It controls visible UI cells and uses native Unity selection; it does not bundle or call original plugin classes.

## Remaining concerns and limitations

**Visual presentation — review advised.** `source/src/InventoryDropdownUi.cs` contains references to a "reference GIF", recreating the original dropdown's style, and matching the original's option-order presentation; it uses the shared caption `-- NO FILTER --`. Although there is no original graphic asset embedded in the release, the overall visual expression could resemble the original. To reduce this ambiguity, compare in-game screenshots and differentiate any distinctive ornamental layout, colors, or typography when practicable. Merely deleting comments does not change substantive visual similarity.

**Not formally clean-room.** Earlier development involved examining the compiled original and observing its behavior. The lack of matching substantial IL and the independent architecture are encouraging, but no one can certify that all implementation choices were reached without reference to the original. Avoid claiming that the original was never studied.

**Source unavailable.** The audit used the original **binary**, not its author-controlled source tree, so source comments, noncompiled material, build scripts, or materials not included in this particular binary could not be compared. Other plugin versions may differ.

**General platform/legal limits.** The original Nexus page says permission is required to modify its files and to reuse its assets and disallows redistributing its uploaded file. An independent work that does not include those files or protected expression is a different question from redistributing/patching the original; Nexus can nevertheless investigate complaints. Brazilian Law 9,609/1998, Art. 6(III) recognizes that resemblance arising from functional/technical constraints alone is not necessarily infringement; application to specific facts needs legal evaluation.

**Other rights:** This is not a clearance of the game's own art/UI, publisher terms, font licenses, trademarks, or third-party libraries. The release includes no separate external image/font files, but screenshots used for promotional purposes should still respect game/platform rules. Plugin loader and game references are linked, not bundled.

## Pre-publication checklist

- [x] Independent compiled plugin DLL and BepInEx plugin ID.
- [x] No original mod DLL inside the release archive.
- [x] No embedded art/images or original compiled resources detected.
- [x] No matching substantive IL methods identified by the specified tests.
- [x] Different UI and item-stat implementations confirmed by method-call analysis.
- [ ] Review **side-by-side in-game appearance** for unusually distinctive copied presentation.
- [ ] Credit **Su4enka's ZeroParades Clothing Filter** for inspiration, without suggesting affiliation or endorsement.
- [ ] Present as a new mod, **not an official update, patch, fork, or redistributable copy** of the original.
- [ ] If releasing source, select a license for your independently authored contributions and document third-party requirements; do not purport to license the game's or another author's materials.
- [ ] Keep a copy of the release ZIP, original binary hashes, and this audit for provenance.

### Public references

- Original Nexus page and permissions: https://www.nexusmods.com/zeroparadesfordeadspies/mods/2
- Brazilian software law, Art. 6(III): https://www2.camara.leg.br/legin/fed/lei/1998/lei-9609-19-fevereiro-1998-364738-normaatualizada-pl.html

---
**Overall technical verdict:** **Low indication of direct code/binary copying in the reviewed v1.0.0 release**; **visual-presentation similarity remains the main unresolved question**. This is a bounded evidentiary assessment, not a legal determination of noninfringement.
