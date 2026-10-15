using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using ZAUM.C4.UI.Inventory;

namespace ZPInventoryFilter;

/// <summary>
/// Conservative one-attempt-per-filter+tab UI selection with verification.
/// Only uses the existing Unity Selectable.Select() pathway AFTER confirming
/// that a user's own item-cell selection updates m_currentItemCell. This class
/// never invokes the inventory manager, equip/unequip, ItemCell.SelectItem(),
/// OnPointerClick(), Harmony, or save/load functions.
/// </summary>
internal static class SafeAutoSelection
{
    private static InventoryUIController? _owner;
    private static bool _observedNativeSelection;
    private static bool _lockedForSession;
    private static int _lastObservedCell;
    private static int _epoch;
    private static int _lastTab = -1;
    private static int _lastAttemptEpoch = -1;
    private static int _lastAttemptTab = -1;
    private static int _changedAtFrame;
    private static bool _pending;
    private static string _expectedId = "";
    private static int _expectedCellInstance;
    private static int _sentAtFrame;
    private static int _attemptEpoch;

    internal static void OnControllerChanged(InventoryUIController? owner)
    {
        if (_owner == owner) return;
        _owner = owner;
        _observedNativeSelection = false;
        _lastObservedCell = 0;
        _lastTab = -1;
        // A reopened inventory should be eligible for one newly scoped attempt,
        // while preserving the session-wide safety lock after verification failures.
        _lastAttemptEpoch = -1;
        _lastAttemptTab = -1;
        _pending = false;
        _expectedId = "";
        _changedAtFrame = Time.frameCount;
        // Keep lockouts across menu closes/reopens within the same game session.
    }

    internal static void OnFilterChanged()
    {
        _epoch++;
        _changedAtFrame = Time.frameCount;
        // Do not force a new attempt while verification from a prior one is pending.
    }

    internal static void ObserveAndVerify(InventoryUIController controller)
    {
        if (!Plugin.AutoSelectionEnabled.Value) return;
        try
        {
            ItemCell? gameCurrent = controller.m_currentItemCell;
            GameObject? focus = EventSystem.current?.currentSelectedGameObject;
            ItemCell? focusedCell = focus != null ? focus.GetComponent<ItemCell>() : null;
            if (focusedCell != null && gameCurrent != null &&
                focusedCell.GetInstanceID() == gameCurrent.GetInstanceID())
            {
                int id = gameCurrent.GetInstanceID();
                if (id != _lastObservedCell)
                {
                    _lastObservedCell = id;
                    _observedNativeSelection = true;
                }
            }

            if (!_pending || Time.frameCount - _sentAtFrame < 3) return;
            _pending = false;
            if (_attemptEpoch != _epoch)
            {
                return;
            }
            string actualId = SafeId(controller.m_currentItemCell);
            int actualCell = controller.m_currentItemCell != null ? controller.m_currentItemCell.GetInstanceID() : 0;
            string focusName = EventSystem.current?.currentSelectedGameObject?.name ?? "none";
            bool selectedOk = actualId == _expectedId && actualCell == _expectedCellInstance;
            if (!selectedOk)
            {
                _lockedForSession = true;
                Plugin.Logger.LogWarning($"[SafeSelection] UNVERIFIED after native Select();expected={_expectedId}:{_expectedCellInstance};actual={actualId}:{actualCell};focus={focusName};AUTO DISABLED for this game session;visual fallback retained");
            }
        }
        catch (Exception e)
        {
            _pending = false;
            _lockedForSession = true;
            Plugin.Logger.LogWarning("[SafeSelection] Probe/verification error; auto disabled for this session: " + e.GetType().Name + ": " + e.Message);
        }
    }

    internal static void AfterVisualFilter(InventoryUIController controller, List<ItemCell> targets,
        int currentTab, bool visualShield,
        Func<ItemCell, bool?> matchesFromVisualCache)
    {
        if (!Plugin.AutoSelectionEnabled.Value || _lockedForSession || _pending || !visualShield) return;
        if (currentTab != 0 && currentTab != 2 && currentTab != 3) return;
        if (_lastTab != currentTab)
        {
            _lastTab = currentTab;
            _changedAtFrame = Time.frameCount;
        }
        if (_lastAttemptEpoch == _epoch && _lastAttemptTab == currentTab) return;
        if (Time.frameCount - _changedAtFrame < 12) return;
        if (!_observedNativeSelection)
        {
            // A single diagnostic line; do not attempt without evidence from
            // a genuine manually selected ItemCell in this inventory session.
            if (_lastAttemptEpoch != _epoch || _lastAttemptTab != currentTab)
            {
                _lastAttemptEpoch = _epoch;
                _lastAttemptTab = currentTab;
            }
            return;
        }
        ItemCell? candidate = PickCandidate(targets, matchesFromVisualCache);
        if (candidate == null)
        {
            _lastAttemptEpoch = _epoch;
            _lastAttemptTab = currentTab;
            return;
        }
        string id = SafeId(candidate);
        if (id.Length == 0) return;
        // Never dispatch a selection to a UI cell that does not belong to our
        // current visible target snapshot or to one whose item has changed.
        if (!candidate.gameObject.activeInHierarchy || !candidate.IsActive() || !candidate.IsInteractable()) return;
        _lastAttemptEpoch = _epoch;
        _lastAttemptTab = currentTab;
        _expectedId = id;
        _expectedCellInstance = candidate.GetInstanceID();
        _sentAtFrame = Time.frameCount;
        _attemptEpoch = _epoch;
        try
        {
            // Matches the standard Unity EventSystem/Selectable focus pathway.
            // Does NOT synthesize pointer clicks or invoke item manager methods.
            candidate.Select();
            _pending = true;
        }
        catch (Exception e)
        {
            _pending = false;
            _lockedForSession = true;
            Plugin.Logger.LogWarning("[SafeSelection] UI selection call failed; auto disabled for session; visual guard retained: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static ItemCell? PickCandidate(List<ItemCell> targets, Func<ItemCell, bool?> matchesFromVisualCache)
    {
        ItemCell? fallbackRecent = null;
        foreach (var c in targets)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if (!c.IsActive() || !c.IsInteractable() || SafeId(c).Length == 0) continue;
            // Conservative matching: unknown and negative are NOT eligible.
            if (matchesFromVisualCache(c) != true) continue;
            // Prefer the actual category cell over the Recently Added mirror.
            if (InventoryCellScope.Classify(c) != "recent") return c;
            if (fallbackRecent == null) fallbackRecent = c;
        }
        return fallbackRecent;
    }

    private static string SafeId(ItemCell? cell)
    {
        try { return cell?.ItemID ?? ""; } catch { return ""; }
    }
}
