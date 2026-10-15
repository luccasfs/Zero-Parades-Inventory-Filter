using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ZPInventoryFilter;

[BepInPlugin(Id, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Id = "zp.inventory.filter";
    public const string Name = "Zero Parades Inventory Filter";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> FilterVisuals = null!;
    internal static ConfigEntry<bool> EnableAllTabFiltering = null!;
    internal static ConfigEntry<bool> EnableToolsTabFiltering = null!;
    internal static ConfigEntry<bool> AutoSelectionEnabled = null!;
    internal static ConfigEntry<float> UiX = null!;
    internal static ConfigEntry<float> UiY = null!;
    internal static ConfigEntry<string> FontName = null!;
    internal static ConfigEntry<bool> InheritCanvasSorting = null!;
    internal static ConfigEntry<int> ManualCanvasSortingOrder = null!;
    internal static ConfigEntry<bool> YieldToTabTooltips = null!;
    internal static ConfigEntry<bool> HideFloatingSelectedArtwork = null!;

    public override void Load()
    {
        Logger = Log;
        Enabled = Config.Bind("General", "Enabled", true,
            "Enable Inventory Filter.");
        FilterVisuals = Config.Bind("Safety", "EnableVisualFiltering", true,
            "Hide inventory cells without a confirmed positive skill bonus. Does not modify inventory items or save data.");
        EnableAllTabFiltering = Config.Bind("Safety", "EnableAllTabVisualFiltering", true,
            "Filter positive skill bonuses in ALL (including native Recently Added cells). Drugs without skill bonuses do not match. Requires EnableVisualFiltering.");
        EnableToolsTabFiltering = Config.Bind("Safety", "EnableToolsTabVisualFiltering", true,
            "Enable skill filtering on the Tools tab. The Drugs tab is never filtered.");
        AutoSelectionEnabled = Config.Bind("Safety", "EnableSafeAutoSelectionTrial", true,
            "Select one compatible item through native Unity UI focus per filter/tab change. Requires observed manual UI selection; disables itself for this session after failed verification. Does not equip or change inventory items.");
        UiX = Config.Bind("Interface", "OriginalStyleOffsetX", 56f,
            "Horizontal positioning adjustment for the minimal inventory caption; larger values move it left.");
        UiY = Config.Bind("Interface", "OriginalStyleOffsetY", -24f,
            "Vertical positioning adjustment for the inventory caption; more negative values move it upward.");
        FontName = Config.Bind("Interface", "FontAssetName", "",
            "Optional TMP font asset name. Leave empty to match the game item-description font automatically.");
        InheritCanvasSorting = Config.Bind("Interface", "InheritCanvasSorting", true,
            "Use the inventory's canvas draw order so the game's native tab hover tooltips can appear above this selector. Does NOT move or resize the selector. Set false only if the selector is obscured in your game.");
        ManualCanvasSortingOrder = Config.Bind("Interface", "ManualCanvasSortingOrder", 490,
            "Fallback only when InheritCanvasSorting=false. 490 reproduces the old overlay order; try smaller values if a game tooltip is behind the selector.");
        YieldToTabTooltips = Config.Bind("Interface", "YieldToTabTooltips", true,
            "Temporarily hide the CLOSED filter caption when hovering a native inventory category tab, so its tooltip is fully legible. Restores on exit, without moving the selector. Does not affect the open dropdown.");
        HideFloatingSelectedArtwork = Config.Bind("Interface", "HideFloatingSelectedArtwork", true,
            "Hide only the floating selected-item artwork over the grid on ALL/Clothing/Tools, including NO FILTER. Normal item cells and right-side details remain visible.");

        // The ONLY injected component contains Unity lifecycle callbacks.
        // No Harmony patches, no inventory/saving logic, no injected generic/reflection methods.
        ClassInjector.RegisterTypeInIl2Cpp<FilterBehaviour>();
        var host = new GameObject("ZPInventoryFilter_Host");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<FilterBehaviour>();

        Logger.LogInfo("Inventory Filter v1.0.0 loaded. UI-only skill filtering for ALL, Clothing and Tools; Drugs untouched. No Harmony hooks or inventory mutations.");
    }
}

public sealed class FilterBehaviour : MonoBehaviour
{
    public FilterBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update() => FilterCore.Update();
    private void LateUpdate()
    {
        FilterCore.EnforceDetailsGuard();
        InventoryDropdownUi.UpdateTooltipAvoidance();
    }
    private void OnDestroy() => FilterCore.Shutdown();
}
