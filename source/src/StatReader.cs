using System;
using ZAUM.FELD.C4.Dialogues;
using ZAUM.C4.UI.Inventory;
using ZAUM.FELD.C4.Dialogues.Model.Entities;

namespace ZPInventoryFilter;

/// <summary>
/// Read-only, strongly-typed inspection of the same gear modifier collection
/// that the game's ModifiersController consumes for item details.
/// NEVER resolves via inventory manager or calls game mutators.
/// null = undecidable; fail open and leave cell visible.
/// </summary>
internal static class StatReader
{
    // Resolve real internal IDs (e.g. inference, muscle) to the game's displayed
    // SkillType using the SAME game helper that the legacy mod referenced.
    // A failed resolution must NEVER be mistaken for a negative match.
    private static bool TryResolveSkillType(string id, out SkillType skill)
    {
        skill = default;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { return C4ProjectUtils.TryGetSkillType(id, out skill); }
        catch { return false; }
    }

    private static bool TryParseChosenSkill(string label, out SkillType skill)
    {
        // The original UI displays "Doppelgäng" (with an umlaut), while the
        // game's SkillType enum uses "Doppelgang" without a diacritic.
        // Translate display text -> internal identifier only at this boundary.
        var name = (label ?? "").Replace(" ", "");
        if (name.Equals("Doppelgäng", StringComparison.OrdinalIgnoreCase))
            name = "Doppelgang";
        return Enum.TryParse(name, true, out skill);
    }

    public static bool? HasPositiveStat(ItemCell cell, string skillName)
    {
        try
        {
            RtItemEntity? entity = cell.m_itemEntity;
            if (entity == null) return null;
            ModifiersCollection? collection = entity.GearModifiersCollection;
            if (collection == null) return null;
            var modifiers = collection.SkillModifiers;
            if (modifiers == null) return null;

            if (!TryParseChosenSkill(skillName, out SkillType wanted)) return null;
            bool found = false;
            bool unknownValue = false;
            int count = Math.Min(modifiers.Count, 256);
            if (modifiers.Count > count) unknownValue = true;
            for (int i = 0; i < count; i++)
            {
                SkillFeatureModifier modifier = modifiers[i];
                if (modifier == null) { unknownValue = true; continue; }
                string id = modifier.ID ?? "";
                if (!TryResolveSkillType(id, out SkillType resolved))
                { unknownValue = true; continue; }
                if (resolved == wanted && modifier.Value > 0) found = true;
            }
            if (found) return true;
            return unknownValue ? null : false;
        }
        catch
        {
            // Incomplete IL2CPP proxy or missing game content => never hide.
            return null;
        }
    }

}
