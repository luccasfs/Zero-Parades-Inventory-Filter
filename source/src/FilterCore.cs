using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ZAUM.C4.UI.Inventory;

namespace ZPInventoryFilter;

/// <summary>
/// UI-only observer. This file deliberately NEVER references the game inventory manager,
/// any save object, AddNewItem, RemoveItem, equip methods, or Harmony.
/// All inspection occurs after the UI has already constructed its ItemCells.
/// </summary>
internal static class FilterCore
{
    private static readonly string[] Skills =
    {
        "Coordination", "Doppelgäng", "Instincts", "Sensors", "Shadowplay",
        "Blueprints", "Cold Read", "Nerve", "Personalism", "Statehood",
        "Entanglement", "Grey Matter", "Poetics", "Records", "Technoflex"
    };

    private static InventoryUIController? _controller;
    private static int _nextScanFrame;
    // Rare global discovery. The inventory controller is retained while its GameObject
    // is hidden; never enumerate every object in Unity during normal gameplay.
    private static readonly List<InventoryUIController> CachedControllers = new();
    private static int _nextDiscoveryFrame;
    private static readonly List<ItemCell> CachedListCells = new();
    private static InventoryUIController? _cachedListOwner;
    private static int _nextCellRescanFrame;
    private static int _observedGameTab = -1;
    private static string _mode = "All";
    private static string _selected = "All items";
    private static string _normalizedSelected = "allitems";
    private static readonly Dictionary<int, VisualMask> Masks = new();
    private static readonly Dictionary<string, bool?> MatchCache = new(StringComparer.Ordinal);
    private static readonly List<ItemCell> VisibleCells = new();
    private static readonly List<ItemCell> FilterTargets = new();
    private static readonly HashSet<int> SeenTargetIds = new();
    private static readonly List<int> StaleMaskIds = new();
    private static int _evaluated;
    private static int _lastFailureLogFrame = -2000;
    private static CanvasGroup? _guardedDetails;
    private static float _savedDetailsAlpha = 1f;
    private static bool _detailsGuardOn;
    // Separate, game-owned UIImage which renders the enlarged selected item
    // above the inventory grid. This is not a child of ItemCell and is not
    // hidden by the cell's CanvasGroup or the details panel's CanvasGroup.
    private static CanvasGroup? _guardedSelectedArtwork;
    private static float _savedSelectedArtworkAlpha = 1f;
    private static bool _savedSelectedArtworkBlocksRaycasts = true;
    private static bool _selectedArtworkGuardOn;
    // The native selected-item art is distinct from the normal inventory ItemCell
    // and the right-hand item details artwork. It can appear like a duplicate when
    // the selected cell is mirrored in Recently Added or its grid gets compacted.
    // Keep this separate from the stale-details guard and NEVER mask grid mirrors.
    private static bool ShouldHideFloatingArtwork(InventoryUIController controller)
    {
        if (!Plugin.FilterVisuals.Value || !Plugin.HideFloatingSelectedArtwork.Value) return false;
        try
        {
            int tab = controller.m_tabContentController.m_currentTabIndex;
            return tab == 0 || tab == 2 || (tab == 3 && Plugin.EnableToolsTabFiltering.Value);
        }
        catch { return false; }
    }

    // Restore scrolling after the game's inventory layout has reflowed.
    // Masking changes LayoutElement.ignoreLayout, which can leave ScrollRect's
    // content bounds stale until the game switches tabs. These are *UI only*.
    // Rebuild at most once for each touched ScrollRect, on a later frame,
    // never continuously while the inventory is open.
    private static readonly Dictionary<int, ScrollRect> PendingScrollRefresh = new();
    private static int _scrollRefreshAtFrame = -1;


    internal static string[] Options => AllOptions;
    internal static int IndexOfChoice(string choice) => Array.IndexOf(AllOptions, choice);

    internal static void Update()
    {
        // One-time UI layout refresh after cells are unmasked. Must run even
        // on frames where the expensive inventory scan is skipped.
        TickPendingScrollRefresh();
        if (Time.frameCount < _nextScanFrame) return;
        // Keep the visual response close to one tenth of a second while the
        // inventory is open. Reuse the cached ItemCells and stat matches.
        // Outside the inventory, keep the original low-overhead cadence.
        _nextScanFrame = Time.frameCount + (_controller == null ? 45 : _mode == "Stat" ? 6 : 12);
        try
        {
            if (!Plugin.Enabled.Value)
            {
                RestoreDetailsGuard();
                RestoreMasks();
                _controller = null;
                ClearCachedCells();
                SafeAutoSelection.OnControllerChanged(null);
                InventoryDropdownUi.SetVisible(false, null);
                return;
            }

            InventoryUIController? active = FindInventoryPanel();
            if (active == null)
            {
                RestoreDetailsGuard();
                RestoreMasks();
                _controller = null;
                ClearCachedCells();
                SafeAutoSelection.OnControllerChanged(null);
                InventoryDropdownUi.SetVisible(false, null);
                return;
            }
            if (_controller != active)
            {
                RestoreDetailsGuard();
                RestoreMasks();
                _controller = active;
                MatchCache.Clear();
                ClearCachedCells();
                SafeAutoSelection.OnControllerChanged(active);
                Plugin.Logger.LogInfo("[InventoryFilter] Inventory UI detected; filter panel enabled.");
            }
            Recompute(active);
            // Do not display an overlay over gameplay when inventory controller remains
            // active in the background. Active inventory-list cells are required.
            Transform? anchor = _evaluated > 0 ? FindUiAnchor(active) : null;
            // Drugs are out of scope: keep their native inventory tab as-is.
            bool drugsTab = active.m_tabContentController != null &&
                            active.m_tabContentController.m_currentTabIndex == 1;
            InventoryDropdownUi.SetVisible(anchor != null && !drugsTab, anchor);
            InventoryDropdownUi.Refresh(_selected);
        }
        catch (Exception ex)
        {
            // Fail open: restore visual state and do not propagate any exception into Unity.
            RestoreDetailsGuard();
            RestoreMasks();
            InventoryDropdownUi.SetVisible(false, null);
            if (Time.frameCount - _lastFailureLogFrame >= 1200)
            {
                _lastFailureLogFrame = Time.frameCount;
                Plugin.Logger.LogWarning("[InventoryFilter] UI-only scan failed; restored cell visibility: " + ex);
            }
        }
    }

    private static bool IsActiveInventory(InventoryUIController? c)
    {
        try
        {
            if (c == null || !c.isActiveAndEnabled || !c.gameObject.activeInHierarchy)
                return false;
            var tab = c.m_tabContentController;
            return tab != null && tab.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    private static InventoryUIController? FindInventoryPanel()
    {
        // A cached list avoids repeatedly traversing every loaded Unity object.
        // Check all known controllers: a hidden one may coexist with an active one.
        bool anyKnownControllerAlive = false;
        foreach (var c in CachedControllers)
        {
            if (IsActiveInventory(c)) return c;
            try { if (c != null && c.gameObject != null) anyKnownControllerAlive = true; }
            catch { /* stale IL2CPP reference */ }
        }
        // An existing hidden inventory controller is sufficient: do not run
        // even infrequent full-scene searches while playing outside the inventory.
        if (anyKnownControllerAlive || Time.frameCount < _nextDiscoveryFrame)
            return null;
        _nextDiscoveryFrame = Time.frameCount + 300;
        CachedControllers.Clear();
        var controllers = Resources.FindObjectsOfTypeAll<InventoryUIController>();
        foreach (var c in controllers)
            if (c != null) CachedControllers.Add(c);
        foreach (var c in CachedControllers)
            if (IsActiveInventory(c)) return c;
        return null;
    }

    private static void ClearCachedCells()
    {
        CachedListCells.Clear();
        VisibleCells.Clear();
        FilterTargets.Clear();
        SeenTargetIds.Clear();
        StaleMaskIds.Clear();
        _cachedListOwner = null;
        _nextCellRescanFrame = 0;
        _observedGameTab = -1;
    }

    private static Transform ResolveCellSearchRoot(InventoryUIController controller)
    {
        Transform t = controller.m_tabContentController.transform;
        Transform? near = null;
        // m_tabContentController may be a sibling of the actual ItemCategoryContent.
        // Search their shared inventory group rather than all loaded Unity objects.
        for (int depth = 0; depth < 10 && t != null; depth++, t = t.parent)
        {
            string name = t.gameObject.name;
            if (name.IndexOf("InventoryGroup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("InventoryContentGroup", StringComparison.OrdinalIgnoreCase) >= 0)
                return t;
            if (near == null && name.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0)
                near = t;
        }
        return near ?? controller.transform;
    }

    private static void RefreshCachedCells(InventoryUIController controller)
    {
        if (_cachedListOwner == controller && Time.frameCount < _nextCellRescanFrame)
            return;
        CachedListCells.Clear();
        _cachedListOwner = controller;
        // Infrequent rescan. Filter selections reuse the existing cells immediately.
        // Rebuild the LOCAL inventory-cell cache after equipment/new-item UI
        // changes. No scene-wide scanning; does not run outside the inventory.
        _nextCellRescanFrame = Time.frameCount + 36;
        var scope = ResolveCellSearchRoot(controller);
        var localCells = scope.GetComponentsInChildren<ItemCell>(true);
        var tab = controller.m_tabContentController;
        foreach (var c in localCells)
        {
            if (c == null || IsEquipPreview(c)) continue;
            if (!IsInventoryListCell(c) && !c.transform.IsChildOf(tab.transform)) continue;
            CachedListCells.Add(c);
        }
        // The game's list can be a sibling outside the localized group in some UIs.
        // Only then use the legacy global fallback, once per refresh interval.
        if (CachedListCells.Count == 0)
        {
            var fallback = Resources.FindObjectsOfTypeAll<ItemCell>();
            foreach (var c in fallback)
            {
                if (c == null || IsEquipPreview(c)) continue;
                if (!IsInventoryListCell(c) && !c.transform.IsChildOf(tab.transform)) continue;
                CachedListCells.Add(c);
            }
        }
        if (CachedListCells.Count == 0)
            _nextCellRescanFrame = Time.frameCount + 45; // UI may still be creating cells
    }

    private static void Recompute(InventoryUIController controller)
    {
        if (controller.m_tabContentController == null) return;
        int activeTab = controller.m_tabContentController.m_currentTabIndex;
        if (_observedGameTab != activeTab)
        {
            _observedGameTab = activeTab;
            // Tab switches can rebuild/reparent ItemCells. Invalidate the local
            // snapshot now rather than waiting for a periodic rescan.
            _nextCellRescanFrame = 0;
        }
        RefreshCachedCells(controller);
        VisibleCells.Clear();
        foreach (var cell in CachedListCells)
        {
            if (cell == null) continue;
            try { if (cell.gameObject.activeInHierarchy) VisibleCells.Add(cell); }
            catch { /* UI cell destroyed while rebuilding view */ }
        }
        _evaluated = VisibleCells.Count;
        SafeAutoSelection.ObserveAndVerify(controller);
        // Selected-item details are hidden when the item is excluded from the
        // filtered UI. No SelectItem/OnSelect,
        // OnPointerClick or any inventory-manager operation is called.
        bool visualMode = Plugin.FilterVisuals.Value && _mode == "Stat";
        if (!visualMode)
        {
            // NO FILTER restores all item cells, but no longer releases the
            // *separate* floating selected-item art on the filterable tabs.
            // The floating artwork looks like a second grid cell when selected.
            // Preserve the native right details panel and native selected cell.
            RestoreOnlyDetailsGuard();
            RestoreMasks();
            SetSelectedArtworkGuard(controller, ShouldHideFloatingArtwork(controller));
            return;
        }

        // The details guard is a hard prerequisite. If unavailable, fail open:
        // leave all inventory cells visible instead of exposing stale details.
        CanvasGroup? details = controller.m_itemDetailsCanvasGroup;
        if (details == null)
        {
            RestoreDetailsGuard();
            RestoreMasks();
            return;
        }

        // Tab selection uses the game's active index, not generic AllContents
        // transforms (which appear under every inventory category).
        int tabIndex = controller.m_tabContentController.m_currentTabIndex;
        bool allTab = tabIndex == 0;
        bool clothingTab = tabIndex == 2;
        // Tools uses index 3 in the tested game build.
        // Drugs (index 1) and Other (index 4) are left untouched.
        bool toolsTab = tabIndex == 3 && Plugin.EnableToolsTabFiltering.Value;
        if ((!allTab && !clothingTab && !toolsTab) || (allTab && !Plugin.EnableAllTabFiltering.Value))
        {
            RestoreDetailsGuard();
            RestoreMasks();
            return;
        }

        // Preserve native Recently Added cells: the ALL layout uses them.

        // Work only with existing live UI ItemCells. Categorization uses the
        // item's parent transforms, not the save, inventory manager or any
        // equip/add/remove lifecycle. ALL includes positive-stat tools when
        // applicable. Unknown categories are NEVER hidden.
        FilterTargets.Clear();
        var targets = FilterTargets;
        foreach (var cell in VisibleCells)
        {
            if (clothingTab)
            {
                if (InventoryCellScope.IsMainClothingCell(cell)) targets.Add(cell);
            }
            else if (toolsTab)
            {
                // Only the native ToolsContent cells; never Recently Added mirrors.
                if (InventoryCellScope.Classify(cell) == "tools") targets.Add(cell);
            }
            else
            {
                string category = InventoryCellScope.Classify(cell);
                if (category == "recent" || category == "clothing" ||
                    category == "consumables" || category == "tools" || category == "others")
                    targets.Add(cell);
            }
        }
        // Fail open if category discovery yields no cells or too many cells.
        if (targets.Count == 0 || targets.Count > 150)
        {
            RestoreDetailsGuard();
            RestoreMasks();
            return;
        }

        ItemCell? selected = controller.m_currentItemCell;
        bool selectedIsTarget = false;
        if (selected != null)
        {
            string selectedId = "";
            try { selectedId = selected.ItemID ?? ""; } catch { }
            if (selectedId.Length > 0)
            {
                foreach (var cell in targets)
                {
                    if (string.Equals(selectedId, cell.ItemID, StringComparison.Ordinal))
                    { selectedIsTarget = true; break; }
                }
            }
        }
        // No UI selection is changed. Never leave stale details on screen
        // for an item that is absent from the active filtered list, even when
        // that stale selection originated in another game inventory category.
        // An undecidable (unknown) item IN the list remains visible.
        bool? selectedMatches = selected != null && selectedIsTarget ? CheckItem(selected) : null;
        bool mustShield = selected == null || !selectedIsTarget || selectedMatches == false;
        // Shield incompatible details only when needed; independently hide the
        // game's enlarged *floating* selected-item image even for valid matches.
        // This image is NOT the art inside the right details panel or the grid.
        // BOTH shields must be available, otherwise fail open and show all items.
        bool hideFloatingArtwork = mustShield || ShouldHideFloatingArtwork(controller);
        if (!SetDetailsGuard(details, mustShield) ||
            !SetSelectedArtworkGuard(controller, hideFloatingArtwork))
        {
            RestoreDetailsGuard(); // restores both game-owned visual guards
            RestoreMasks();
            return;
        }

        SeenTargetIds.Clear();
        var seen = SeenTargetIds;
        foreach (var cell in targets)
        {
            int key = cell.GetInstanceID();
            seen.Add(key);
            bool? match = CheckItem(cell);
            SetMask(cell, match == false);
        }
        // Undo masks on cells that are no longer in the selected tab/scope.
        StaleMaskIds.Clear();
        foreach (int key in Masks.Keys)
            if (!seen.Contains(key)) StaleMaskIds.Add(key);
        foreach (int key in StaleMaskIds) Unmask(key);
        SafeAutoSelection.AfterVisualFilter(controller, targets, tabIndex, mustShield, CheckItem);
    }

    // The game may animate details opacity in its own UI loop. Reasserting
    // the zero alpha in LateUpdate keeps stale details hidden without
    // changing the game's inventory selection or contents.
    internal static void EnforceDetailsGuard()
    {
        if (!_detailsGuardOn && !_selectedArtworkGuardOn) return;
        try
        {
            if (_detailsGuardOn && _guardedDetails != null) _guardedDetails.alpha = 0f;
            if (_selectedArtworkGuardOn && _guardedSelectedArtwork != null)
            {
                _guardedSelectedArtwork.alpha = 0f;
                _guardedSelectedArtwork.blocksRaycasts = false; // Invisible overlay must not intercept grid clicks.
            }
        }
        catch { RestoreDetailsGuard(); RestoreMasks(); }
    }

    private static bool SetDetailsGuard(CanvasGroup details, bool shield)
    {
        if (!shield) { RestoreOnlyDetailsGuard(); return true; }
        try
        {
            if (_detailsGuardOn && _guardedDetails != details) RestoreOnlyDetailsGuard();
            if (!_detailsGuardOn)
            {
                _guardedDetails = details;
                _savedDetailsAlpha = details.alpha;
                _detailsGuardOn = true;
            }
            details.alpha = 0f;
            return true;
        }
        catch { RestoreOnlyDetailsGuard(); return false; }
    }

    private static bool SetSelectedArtworkGuard(InventoryUIController controller, bool shield)
    {
        if (!shield) { RestoreSelectedArtworkGuard(); return true; }
        try
        {
            // Render-only access to the game's dedicated selected-item UIImage.
            // Never call HideArtworkItemImage/SetArtworkItemImage or change selection.
            var artwork = controller.m_itemCellArtwork;
            if (artwork == null || artwork.gameObject == null) return false;
            var group = artwork.gameObject.GetComponent<CanvasGroup>();
            if (group == null) group = artwork.gameObject.AddComponent<CanvasGroup>();
            if (group == null) return false;
            if (_selectedArtworkGuardOn && _guardedSelectedArtwork != group)
                RestoreSelectedArtworkGuard();
            if (!_selectedArtworkGuardOn)
            {
                _guardedSelectedArtwork = group;
                _savedSelectedArtworkAlpha = group.alpha;
                _savedSelectedArtworkBlocksRaycasts = group.blocksRaycasts;
                _selectedArtworkGuardOn = true;
            }
            group.alpha = 0f;
            group.blocksRaycasts = false;
            return true;
        }
        catch { RestoreSelectedArtworkGuard(); return false; }
    }

    private static void RestoreSelectedArtworkGuard()
    {
        if (!_selectedArtworkGuardOn) return;
        try
        {
            if (_guardedSelectedArtwork != null)
            {
                _guardedSelectedArtwork.alpha = _savedSelectedArtworkAlpha;
                _guardedSelectedArtwork.blocksRaycasts = _savedSelectedArtworkBlocksRaycasts;
            }
        }
        catch { /* UI may have been recreated */ }
        _selectedArtworkGuardOn = false;
        _guardedSelectedArtwork = null;
    }

    private static void RestoreOnlyDetailsGuard()
    {
        if (!_detailsGuardOn) return;
        try { if (_guardedDetails != null) _guardedDetails.alpha = _savedDetailsAlpha; }
        catch { /* destroyed UI */ }
        _detailsGuardOn = false;
        _guardedDetails = null;
    }

    private static void RestoreDetailsGuard()
    {
        // Exit/failure: restore both game-owned UI elements. Normal selected-
        // item transitions call RestoreOnlyDetailsGuard() and keep artwork hidden.
        RestoreSelectedArtworkGuard();
        RestoreOnlyDetailsGuard();
    }

    private static bool IsInventoryListCell(ItemCell cell)
    {
        Transform? t = cell.transform;
        for (int i = 0; i < 16 && t != null; ++i, t = t.parent)
        {
            string name = t.gameObject.name.ToLowerInvariant();
            if (name.Contains("inventorycontentgroup") || name.Contains("itemcategorycontent")
                || name.Contains("inventorygroup") || name.Contains("inventoryitemcontent"))
                return true;
        }
        return false;
    }

    private static Transform? FindUiAnchor(InventoryUIController controller)
    {
        // Keep the tested position by anchoring to the game's tab controller.
        Transform? anchor = controller.m_tabContentController?.transform;
        return anchor != null && anchor.GetComponent<RectTransform>() != null ? anchor : null;
    }

    private static bool IsEquipPreview(ItemCell cell)
    {
        // Equipped/preview cells are not part of the inventory list and MUST NOT be hidden.
        Transform? t = cell.transform;
        for (int i = 0; i < 4 && t != null; i++, t = t.parent)
        {
            string name = t.gameObject.name.ToLowerInvariant();
            if (name.Contains("equip") || name.Contains("doll") || name.Contains("preview"))
                return true;
        }
        return false;
    }

    private static bool? CheckItem(ItemCell cell)
    {
        if (_mode == "All") return true;
        string id;
        try { id = cell.ItemID ?? ""; }
        catch { return null; }
        string cacheKey = id + "|" + _normalizedSelected;
        if (!string.IsNullOrEmpty(id) && MatchCache.TryGetValue(cacheKey, out bool? cached))
            return cached;
        bool? match = StatReader.HasPositiveStat(cell, _selected);
        if (!string.IsNullOrEmpty(id) && match != null) MatchCache[cacheKey] = match;
        return match;
    }

    private static string Normalize(string s)
    {
        return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static void SetMask(ItemCell cell, bool hidden)
    {
        int key = cell.GetInstanceID();
        if (!hidden) { Unmask(key); return; }
        if (!Masks.TryGetValue(key, out var state))
        {
            // CanvasGroup and LayoutElement ONLY. Never SetActive(false), never SelectItem.
            var group = cell.GetComponent<CanvasGroup>();
            if (group == null) group = cell.gameObject.AddComponent<CanvasGroup>();
            var layout = cell.GetComponent<LayoutElement>();
            if (layout == null) layout = cell.gameObject.AddComponent<LayoutElement>();
            state = new VisualMask(cell, group!, layout!);
            Masks[key] = state;
        }
        state.Hide();
    }

    private static void Unmask(int key)
    {
        if (!Masks.Remove(key, out var state)) return;
        RememberScrollOwner(state);
        state.Restore();
        // A stat-to-stat transition may restore some cells without passing
        // through RestoreMasks(). Refresh bounds after all changed cells settle.
        _scrollRefreshAtFrame = Time.frameCount + 2;
    }

    // Find game-owned scroll views above UI cells whose LayoutElement is about
    // to become visible again. Do not touch our plugin's dropdown scroll view.
    private static void RememberScrollOwner(VisualMask state)
    {
        try
        {
            var node = state.CellTransform;
            for (int depth = 0; node != null && depth < 20; depth++, node = node.parent)
            {
                var scroll = node.GetComponent<ScrollRect>();
                if (scroll == null || scroll.content == null) continue;
                PendingScrollRefresh[scroll.GetInstanceID()] = scroll;
            }
        }
        catch { /* Transient UI element was removed by the game. */ }
    }

    private static void RestoreMasks()
    {
        if (Masks.Count == 0) return;
        foreach (var state in Masks.Values)
        {
            RememberScrollOwner(state);
            state.Restore();
        }
        Masks.Clear();
        if (PendingScrollRefresh.Count == 0) return;

        // Do not force a rebuild during the dropdown click callback itself:
        // let Unity's regular layout pass process ignoreLayout=false first.
        _scrollRefreshAtFrame = Time.frameCount + 2;
        foreach (var scroll in PendingScrollRefresh.Values)
        {
            try
            {
                if (scroll != null && scroll.content != null)
                    LayoutRebuilder.MarkLayoutForRebuild(scroll.content);
            }
            catch { /* Later tick still tries the independent refresh. */ }
        }
    }

    private static void TickPendingScrollRefresh()
    {
        if (_scrollRefreshAtFrame < 0 || Time.frameCount < _scrollRefreshAtFrame) return;
        _scrollRefreshAtFrame = -1;
        if (PendingScrollRefresh.Count == 0) return;
        var scrollers = PendingScrollRefresh.Values.ToArray();
        PendingScrollRefresh.Clear();
        int failed = 0;
        foreach (var scroll in scrollers)
        {
            try
            {
                if (scroll == null || !scroll.isActiveAndEnabled ||
                    !scroll.gameObject.activeInHierarchy || scroll.content == null ||
                    !scroll.content.gameObject.activeInHierarchy) continue;
                LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            }
            catch
            {
                ++failed;
            }
        }
        bool canvasPass = true;
        try { Canvas.ForceUpdateCanvases(); }
        catch { canvasPass = false; /* ScrollRect can still refresh on next UI pass. */ }
        foreach (var scroll in scrollers)
        {
            try
            {
                if (scroll == null || !scroll.isActiveAndEnabled ||
                    !scroll.gameObject.activeInHierarchy || scroll.content == null ||
                    !scroll.content.gameObject.activeInHierarchy) continue;
                // Recalculate the scrollable bounds without toggling tabs or
                // changing scroll position / vertical-enabled configuration.
                scroll.Rebuild(CanvasUpdate.PostLayout);
            }
            catch
            {
                ++failed;
            }
        }
        if (failed > 0 || !canvasPass)
            Plugin.Logger.LogWarning($"[InventoryFilter] Scroll layout refresh incomplete: failures={failed}, canvasUpdated={canvasPass}.");
    }

    internal static void Shutdown()
    {
        RestoreDetailsGuard();
        RestoreMasks();
        ClearCachedCells();
        SafeAutoSelection.OnControllerChanged(null);
        CachedControllers.Clear();
        MatchCache.Clear();
        PendingScrollRefresh.Clear();
        _scrollRefreshAtFrame = -1;
        _controller = null;
        InventoryDropdownUi.Shutdown();
    }

    // A standalone Unity uGUI canvas avoids stripped IMGUI dependencies.
    // These methods only mutate this plugin's filter choice, never game inventory state.
    // The game already supplies equipment slot/category filtering. Only stats here.
    private static readonly string[] AllOptions = new[] { "All items" }.Concat(Skills).ToArray();

    internal static void SelectIndexFromUi(int index) => SelectIndex(index);

    private static void SelectIndex(int index)
    {
        if (index < 0 || index >= AllOptions.Length) return;
        string mode = index == 0 ? "All" : "Stat";
        Pick(mode, AllOptions[index]);
        InventoryDropdownUi.Refresh(_selected);
    }

    private static void Pick(string mode, string value)
    {
        // Keep the prior visual masks during STAT -> STAT transitions until
        // the next quick UI scan applies the new ones. Restoring everything
        // here caused a visible flash of drugs/items for one rendered frame.
        // NO FILTER is handled on the next UI scan by restoring all masks.
        if (mode == "Stat" && _controller != null)
        {
            try
            {
                var details = _controller.m_itemDetailsCanvasGroup;
                if (details != null)
                {
                    SetDetailsGuard(details, true);
                    SetSelectedArtworkGuard(_controller, true);
                }
            }
            catch { /* Next UI scan will fail open if necessary. */ }
        }
        _mode = mode;
        _selected = value;
        _normalizedSelected = Normalize(value);
        MatchCache.Clear();
        SafeAutoSelection.OnFilterChanged();
        _nextScanFrame = 0;
        _nextCellRescanFrame = 0;
    }

    private sealed class VisualMask
    {
        private readonly ItemCell _cell;
        internal Transform CellTransform => _cell.transform;
        private readonly CanvasGroup _group;
        private readonly LayoutElement _layout;
        private readonly float _alpha;
        private readonly bool _interactable, _raycasts, _ignoreLayout;
        private bool _hidden;
        internal VisualMask(ItemCell cell, CanvasGroup group, LayoutElement layout)
        {
            _cell = cell; _group = group; _layout = layout;
            _alpha = group.alpha; _interactable = group.interactable;
            _raycasts = group.blocksRaycasts; _ignoreLayout = layout.ignoreLayout;
        }
        internal void Hide()
        {
            if (_cell == null || _hidden) return;
            _hidden = true;
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            _layout.ignoreLayout = true;
        }
        internal void Restore()
        {
            try
            {
                if (_cell == null || !_hidden) return;
                _hidden = false;
                if (_group != null)
                {
                    _group.alpha = _alpha;
                    _group.interactable = _interactable;
                    _group.blocksRaycasts = _raycasts;
                }
                if (_layout != null) _layout.ignoreLayout = _ignoreLayout;
                // No destroy here: Unity may be enumerating UI layout components.
                // The added components are harmless and become reusable UI-only helpers.
            }
            catch { /* Missing/destroyed UI object: no game state to restore. */ }
        }
    }
}
