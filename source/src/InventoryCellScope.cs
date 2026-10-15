using System;
using UnityEngine;
using ZAUM.C4.UI.Inventory;

namespace ZPInventoryFilter;

/// <summary>Read-only classification of game-created inventory cells. Never changes inventory data.</summary>
internal static class InventoryCellScope
{
    // Order matters: RecentlyAddedContent takes precedence over all its inner
    // scroll-view names. 'AllContents' is intentionally NOT a category marker.
    internal static string Classify(ItemCell cell)
    {
        bool recent = false, clothing = false, consumables = false, tools = false, others = false;
        for (Transform? t = cell.transform; t != null; t = t.parent)
        {
            string name = t.gameObject.name;
            if (name.Equals("RecentlyAddedContent", StringComparison.OrdinalIgnoreCase)) recent = true;
            if (name.Equals("ClothingContent", StringComparison.OrdinalIgnoreCase)) clothing = true;
            if (name.Equals("ConsumablesContent", StringComparison.OrdinalIgnoreCase)) consumables = true;
            if (name.Equals("ToolsContent", StringComparison.OrdinalIgnoreCase)) tools = true;
            if (name.Equals("OthersContent", StringComparison.OrdinalIgnoreCase)) others = true;
        }
        if (recent) return "recent";
        if (clothing) return "clothing";
        if (consumables) return "consumables";
        if (tools) return "tools";
        if (others) return "others";
        return "unclassified";
    }

    internal static bool IsMainClothingCell(ItemCell cell)
    {
        try
        {
            bool withinClothing = false;
            for (Transform? t = cell.transform; t != null; t = t.parent)
            {
                var n = t.gameObject.name;
                // If a cell is inside the recent-items strip, it must NEVER be
                // selected as a clothing-filter candidate, even if duplicated.
                if (n.Equals("RecentlyAddedContent", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (n.Equals("ClothingContent", StringComparison.OrdinalIgnoreCase))
                    withinClothing = true;
            }
            return withinClothing;
        }
        catch { return false; } // fail closed for audit (no item mutations)
    }

}
