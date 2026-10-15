using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Il2CppInterop.Runtime;

namespace ZPInventoryFilter;

/// <summary>
/// Inventory filter selector styled to match Zero Parades' native dossier UI.
///
/// Closed : a dark rounded bar reading "-- NO FILTER --" (or the active filter, tinted with its
///          skill-group colour), sitting between the tab bar and the "C. INVENTORY" caption,
///          centred over the category icon bar.
/// Open   : the same bar expands into a rounded, thin-bordered dark frame. Options are
///          centred, monospaced and coloured by skill group (pink / amber / purple). The
///          active option is shown in the caption and is omitted from the list, exactly like
///          the original. Hover = soft grey rounded highlight. Click outside = close.
///
/// All objects are plugin-owned (no game controls are cloned). The popup is read-only UI:
/// it never touches game item creation, selection, management, or persistence APIs.
/// All sizes below are in canvas pixels at the 1920x1080 reference.
/// </summary>
internal static class InventoryDropdownUi
{
    // ---------- geometry (measured from the reference GIF / 1920x1080 screenshot) ----------
    private const float BarW = 271f;          // closed bar + popup width (icon bar 237 + 17 each side)
    private const float BarH = 32f;           // closed bar height / caption row
    private const float RowH = 35f;           // option pitch (GIF: 14.7px @800w = ~35px @1920w)
    private const float RowPadX = 4f;         // inset of the option list inside the frame
    private const float ListTop = 34f;        // popup-local y where the first option starts

    private const float PopupPadBottom = 5f;
    private const float PopupMaxH = 920f;     // scrolls beyond this
    private const float FontSize = 19f;       // monospace advance ~11.6px, as in the GIF
    private const float CharSpacing = 2f;
    private const int SkillsPerGroup = 5;     // 3 groups x 5 skills (operant / ... / ...)

    // ---------- colours (game palette; list text matches the dossier's group colours) ----------
    private static readonly Color TransparentHit = new(0f, 0f, 0f, 0.002f);
    // Dark, slightly see-through panels (no stripes). The last number is the opacity:
    // lower = more of the inventory shows through, higher = easier to read. The game renders UI in
    // linear colour space, so even small see-through values show bright items quite clearly.
    private static readonly Color BarFill = new(0.030f, 0.030f, 0.035f, 0.85f);
    private static readonly Color PopupFill = new(0.030f, 0.030f, 0.035f, 0.90f);
    private static readonly Color RingColor = new(0.66f, 0.66f, 0.66f, 0.95f);
    private static readonly Color HoverOff = new(1f, 1f, 1f, 0f);
    private static readonly Color RowHover = new(0.55f, 0.55f, 0.55f, 0.22f);
    private static readonly Color RowPressed = new(0.72f, 0.72f, 0.72f, 0.34f);

    private static readonly Color White = new(0.94f, 0.94f, 0.93f, 1f);
    // Group colours = the game's own bright status-bar palette (FTG pink / ANX amber / DEL purple).
    // The dimmer dossier-icon tones (#901A52 / #AB7611 / #5040A0) were too low-contrast as text.
    private static readonly Color Pink = new(0.859f, 0.145f, 0.498f, 1f);  // #DB257F
    private static readonly Color Amber = new(0.96f, 0.67f, 0.04f, 1f);    // ~#F5AB0A
    private static readonly Color Purple = new(0.471f, 0.369f, 0.941f, 1f); // #785EF0


    // ---------- state ----------
    private static GameObject? _root;
    private static GameObject? _head;
    private static GameObject? _popup;
    private static GameObject? _viewport;

    private static RectTransform? _contentRect;
    private static GameObject[] _rows = Array.Empty<GameObject>();
    private static TMP_Text? _headText;
    private static TMP_Text? _popupText;
    private static Transform? _anchor;
    private static RectTransform? _tooltipHoverRect;
    private static Camera? _tooltipHoverCamera;
    private static TMP_FontAsset? _font;
    private static bool _unavailable;
    private static bool _visible;
    private static bool _open;
    private static bool _suppressedForTabHover;
    private static bool _hoverCheckUnsupported;
    private static bool _reportedNoFont;
    private static string _lastSelection = "";

    private static Sprite? _fillSprite;
    private static Sprite? _ringSprite;

    internal static void SetVisible(bool visible, Transform? preferredAnchor)
    {
        _visible = visible;
        if (!visible || preferredAnchor == null)
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            SetOpen(false);
            return;
        }
        if (_unavailable) return;
        if (_root == null || _anchor != preferredAnchor)
        {
            Shutdown();
            Create(preferredAnchor);
        }
        if (_root != null && !_root.activeSelf) _root.SetActive(true);
    }

    internal static void Refresh(string choice)
    {
        if (!_visible || _root == null) return;
        string caption = string.Equals(choice, "All items", StringComparison.OrdinalIgnoreCase)
            ? "NO FILTER" : (choice ?? "NO FILTER").ToUpperInvariant();
        if (_lastSelection == caption) return;
        _lastSelection = caption;

        int index = FilterCore.IndexOfChoice(choice ?? "All items");
        if (index < 0) index = 0;
        string shown = index == 0 ? "-- NO FILTER --" : caption;
        Color tone = ToneFor(index);
        SetCaption(_headText, shown, tone);
        SetCaption(_popupText, shown, tone);
        Relayout(index);
    }

    /// <summary>
    /// Keep the inventory tooltip legible without changing selector position or
    /// canvas stacking: temporarily hide the CLOSED selector while the pointer
    /// hovers a native inventory category icon. This never touches game UI objects.
    /// The expanded popup takes priority, so navigating filter options still works.
    /// One inexpensive cursor/RectTransform check per frame; no hierarchy scan.
    /// </summary>
    internal static void UpdateTooltipAvoidance()
    {
        if (_root == null || !_visible || _head == null || _hoverCheckUnsupported)
            return;
        bool suppress = false;
        try
        {
            if (Plugin.YieldToTabTooltips.Value && !_open && _root.activeInHierarchy && _tooltipHoverRect != null)
            {
                Vector2 pointer = UnityEngine.Input.mousePosition;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_tooltipHoverRect, pointer, _tooltipHoverCamera, out Vector2 local))
                {
                    // Closed bar: local X [-271..0], Y [-32..0]. Native icon row
                    // in the 1920x1080 reference: X roughly [-247..-40],
                    // Y roughly [-91..-49] (immediately below this caption).
                    // Wide enough for any of the five category icon hover zones.
                    suppress = local.x >= -254f && local.x <= -28f &&
                               local.y >= -106f && local.y <= -39f;
                }
            }
        }
        catch (Exception ex)
        {
            // A stripped legacy Input API must not break the inventory UI.
            _hoverCheckUnsupported = true;
            Plugin.Logger.LogWarning("[InventoryFilter] Native-tab tooltip hover avoidance unavailable; using ordinary canvas order: " + ex.Message);
        }
        if (_suppressedForTabHover == suppress) return;
        _suppressedForTabHover = suppress;
        if (_head != null && _head.activeSelf == suppress)
            _head.SetActive(!suppress);
    }

    private static void HideMenu() => SetOpen(false);

    private static void ToggleMenu() => SetOpen(!_open);

    /// <summary>
    /// Open = popup shown, closed bar hidden (the popup's own caption row replaces it, so no square
    /// corner of the closed bar can poke out from behind the popup's rounded frame).
    /// </summary>
    private static void SetOpen(bool open)
    {
        _open = open;
        if (_popup != null && _popup.activeSelf != open) _popup.SetActive(open);
        bool showHead = !open && !_suppressedForTabHover;
        if (_head != null && _head.activeSelf != showHead) _head.SetActive(showHead);
    }

    private static void SetCaption(TMP_Text? label, string value, Color tone)
    {
        if (label == null) return;
        label.text = value;
        label.color = tone;
    }

    /// <summary>Text colour for NO FILTER and the three skill groups.</summary>
    private static Color ToneFor(int index)
    {
        if (index <= 0) return White;

        int group = (index - 1) / SkillsPerGroup;
        return group == 0 ? Pink : group == 1 ? Amber : Purple;
    }

    private static void Create(Transform anchor)
    {
        try
        {
            _anchor = anchor;
            _font = ResolveFont(anchor);
            if (_font == null)
            {
                if (!_reportedNoFont)
                {
                    _reportedNoFont = true;
                    Plugin.Logger.LogWarning("[InventoryFilter] Game font not ready; caption will retry on next inventory refresh.");
                }
                return;
            }
            EnsureSprites();

            // Same anchor/Canvas as v0.1.4/v0.1.5 (proven stable in game); only the offsets changed.
            // Top-right pivot: anchor right edge -83.5px => bar centred over the icon bar;
            // anchor top +31px => bar between the tab strip and the "C. INVENTORY" caption.
            // OriginalStyleOffsetX/Y still work: +X moves LEFT, more negative Y moves UP (1 unit = 1px).
            _root = Make("ZP_IF_InventoryCaption", anchor);
            var rt = Rect(_root);
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(BarW, BarH);
            rt.anchoredPosition = new Vector2(-Plugin.UiX.Value - 27.5f, -Plugin.UiY.Value + 7f);
            _root.AddComponent<LayoutElement>().ignoreLayout = true;

            var canvas = _root.AddComponent<Canvas>();
            // The previous hardcoded 490 forced this nested canvas above the game's
            // tab-hover tooltips. Inherited sorting retains the SAME RectTransform,
            // local draw order and raycasting, but allows higher-level native game
            // tooltips to render in front. Do not move/reparent the selector.
            bool inheritSorting = Plugin.InheritCanvasSorting.Value;
            canvas.overrideSorting = !inheritSorting;
            if (!inheritSorting)
                canvas.sortingOrder = Plugin.ManualCanvasSortingOrder.Value;
            _root.AddComponent<GraphicRaycaster>();
            _tooltipHoverRect = rt;
            Canvas? parentCanvas = canvas.rootCanvas;
            _tooltipHoverCamera = parentCanvas != null && parentCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? parentCanvas.worldCamera : null;
            Plugin.Logger.LogInfo("[InventoryFilter] UI canvas sorting=" +
                (inheritSorting ? "inherited (native tooltip priority)" :
                 "manual order " + canvas.sortingOrder) +
                "; inventory anchor unchanged.");

            BuildClosedBar(rt);
            BuildPopup(rt);

            SetOpen(false);
            _lastSelection = "";
            Relayout(0);
            Plugin.Logger.LogInfo("[InventoryFilter] GIF-style selector created (plugin-owned, no game hooks).");
        }
        catch (Exception ex)
        {
            _unavailable = true;
            Plugin.Logger.LogError("[InventoryFilter] Inventory-caption UI failed to construct; disabled safely: " + ex);
            Shutdown();
        }
    }

    // ------------------------------------------------------------------------------------
    // Closed state: dark rounded bar + "-- NO FILTER --"
    // ------------------------------------------------------------------------------------
    private static void BuildClosedBar(RectTransform root)
    {
        var head = Make("ClosedBar", root);
        _head = head;
        SetRect(head, 0f, 0f, BarW, BarH);
        var barImage = AddImage(head, BarFill, true);
        barImage.sprite = _fillSprite;          // same rounded shape as the open frame
        barImage.type = Image.Type.Sliced;

        _headText = Label(head.transform, "InventoryFilterText", "-- NO FILTER --",
            0f, 0f, BarW, BarH, FontSize, White, TextAlignmentOptions.Center, CharSpacing);
        AddClickable(head, ToggleMenu);
    }

    // ------------------------------------------------------------------------------------
    // Open state: rounded dark frame, caption row, coloured option list
    // ------------------------------------------------------------------------------------
    private static void BuildPopup(RectTransform root)
    {
        _popup = Make("InventoryFilterOptions", root);
        SetRect(_popup, 0f, 0f, BarW, BarH);
        var frame = AddImage(_popup, PopupFill, true);
        frame.sprite = _fillSprite;
        frame.type = Image.Type.Sliced;

        // Click-outside-to-close: a huge, invisible raycast target underneath everything else in the popup.
        var blocker = Make("OutsideClickCatcher", _popup.transform);
        var br = Rect(blocker);
        br.anchorMin = new Vector2(0.5f, 0.5f);
        br.anchorMax = new Vector2(0.5f, 0.5f);
        br.pivot = new Vector2(0.5f, 0.5f);
        br.anchoredPosition = Vector2.zero;
        br.sizeDelta = new Vector2(6000f, 4000f);
        AddImage(blocker, TransparentHit, true);
        AddClickable(blocker, HideMenu);

        var ring = Make("Border", _popup.transform);
        Stretch(ring, 0f);
        var ringImage = AddImage(ring, RingColor, false);
        ringImage.sprite = _ringSprite;
        ringImage.type = Image.Type.Sliced;
        ringImage.fillCenter = false;

        // Caption row = the same bar, now inside the frame. Clicking it closes the list.
        var caption = Make("Caption", _popup.transform);
        SetRect(caption, 0f, 0f, BarW, BarH);
        AddImage(caption, TransparentHit, true);
        _popupText = Label(caption.transform, "Text", "-- NO FILTER --",
            0f, 0f, BarW, BarH, FontSize, White, TextAlignmentOptions.Center, CharSpacing);
        AddClickable(caption, ToggleMenu);

        _viewport = Make("Viewport", _popup.transform);
        SetRect(_viewport, RowPadX, -ListTop, BarW - RowPadX * 2f, 100f);
        AddImage(_viewport, TransparentHit, true);
        _viewport.AddComponent<RectMask2D>();

        var content = Make("Content", _viewport.transform);
        _contentRect = Rect(content);
        _contentRect.anchorMin = new Vector2(0f, 1f);
        _contentRect.anchorMax = new Vector2(0f, 1f);
        _contentRect.pivot = new Vector2(0f, 1f);
        _contentRect.anchoredPosition = Vector2.zero;

        float rowW = BarW - RowPadX * 2f;
        string[] options = FilterCore.Options;
        _rows = new GameObject[options.Length];
        for (int i = 0; i < options.Length; ++i)
        {
            int index = i;
            var item = Make("Option_" + i, content.transform);
            SetRect(item, 0f, 0f, rowW, RowH);
            AddImage(item, TransparentHit, true);   // hit-testing only

            // Rounded grey hover pill (the Button tints this child, not the row itself).
            var pill = Make("Hover", item.transform);
            SetRect(pill, 1f, -4.5f, rowW - 2f, RowH - 9f);
            var pillImage = AddImage(pill, Color.white, false);
            pillImage.sprite = _fillSprite;
            pillImage.type = Image.Type.Sliced;

            var button = AddClickable(item, () =>
            {
                FilterCore.SelectIndexFromUi(index);
                HideMenu();
            });
            button.targetGraphic = pillImage;
            var colors = button.colors;
            colors.normalColor = HoverOff;
            colors.highlightedColor = RowHover;
            colors.pressedColor = RowPressed;
            colors.selectedColor = HoverOff;      // no sticky highlight after a click
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Label(item.transform, "Text", i == 0 ? "-- NO FILTER --" : options[i].ToUpperInvariant(),
                0f, 0f, rowW, RowH, FontSize, ToneFor(i), TextAlignmentOptions.Center, CharSpacing);
            _rows[i] = item;
        }


        var scroll = _viewport.AddComponent<ScrollRect>();
        scroll.viewport = Rect(_viewport);
        scroll.content = _contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.scrollSensitivity = 24f;
    }

    /// <summary>
    /// Lays the options out top-to-bottom, skipping the active one (it lives in the caption),
    /// and sizes the frame to fit. Pure UI bookkeeping on plugin-owned objects.
    /// </summary>
    private static void Relayout(int selected)
    {
        if (_popup == null || _viewport == null || _contentRect == null) return;

        float rowW = BarW - RowPadX * 2f;
        float y = 0f;
        for (int i = 0; i < _rows.Length; ++i)
        {
            var row = _rows[i];
            if (row == null) continue;
            if (i == selected)
            {
                if (row.activeSelf) row.SetActive(false);
                continue;
            }

            SetRect(row, 0f, -y, rowW, RowH);
            if (!row.activeSelf) row.SetActive(true);
            y += RowH;
        }
        _contentRect.sizeDelta = new Vector2(rowW, y);

        float popupH = Math.Min(ListTop + y + PopupPadBottom, PopupMaxH);
        Rect(_popup).sizeDelta = new Vector2(BarW, popupH);
        Rect(_viewport).sizeDelta = new Vector2(rowW, popupH - ListTop - PopupPadBottom);
    }

    // ------------------------------------------------------------------------------------
    // Procedural sprites (no game assets touched): rounded fill and rounded 1.6px ring
    // ------------------------------------------------------------------------------------
    private static void EnsureSprites()
    {
        if (_fillSprite == null) _fillSprite = BuildRounded(false);
        if (_ringSprite == null) _ringSprite = BuildRounded(true);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static Sprite BuildRounded(bool ring)
    {
        const int size = 24;
        const float radius = 8f;
        const float thickness = 1.6f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        float half = size * 0.5f;
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                // Signed distance to a rounded square centred in the texture (negative inside).
                float px = Math.Abs(x + 0.5f - half) - (half - radius);
                float py = Math.Abs(y + 0.5f - half) - (half - radius);
                float ox = Math.Max(px, 0f);
                float oy = Math.Max(py, 0f);
                float dist = (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(px, py), 0f) - radius;
                float outer = Clamp01(0.5f - dist);
                float alpha = ring ? outer - Clamp01(0.5f - (dist + thickness)) : outer;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var sprite = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u,
            SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // ------------------------------------------------------------------------------------
    // Font: the original list uses the same monospaced face as the item-description readout.
    // ------------------------------------------------------------------------------------
    private static TMP_FontAsset? ResolveFont(Transform anchor)
    {
        string wanted = (Plugin.FontName.Value ?? "").Trim();
        TMP_FontAsset? found = null;
        string source = "";

        if (wanted.Length > 0)
        {
            found = FindFontByName(wanted);
            if (found != null) source = "config FontAssetName";
            else Plugin.Logger.LogWarning("[InventoryFilter] FontAssetName '" + wanted + "' not found; using automatic font.");
        }
        if (found == null)
        {
            found = FindReadoutFont(anchor);
            if (found != null) source = "item description readout";
        }
        if (found == null)
        {
            found = FindMonospaceFont();
            if (found != null) source = "monospace name match";
        }
        if (found == null)
        {
            found = FindGameFont(anchor);
            if (found != null) source = "inventory heading fallback";
        }
        if (found != null)
            Plugin.Logger.LogInfo("[InventoryFilter] Selector font: '" + found.name + "' (" + source + ").");
        return found;
    }

    private static TMP_FontAsset? FindFontByName(string wanted)
    {
        TMP_FontAsset? partial = null;
        foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (f == null) continue;
            string n = f.name ?? "";
            if (string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) return f;
            if (partial == null && n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) partial = f;
        }
        return partial;
    }

    private static bool IsOwnObject(Transform? t)
    {
        for (int i = 0; i < 10 && t != null; ++i, t = t.parent)
            if (t.gameObject.name.StartsWith("ZP_IF_", StringComparison.Ordinal)) return true;
        return false;
    }

    private static TMP_FontAsset? FindReadoutFont(Transform anchor)
    {
        try
        {
            Canvas? canvas = anchor.GetComponentInParent<Canvas>();
            Transform scope = canvas != null ? canvas.rootCanvas.transform : anchor.root;
            TMP_Text? best = null;
            int bestLen = 0;
            foreach (var t in scope.GetComponentsInChildren<TMP_Text>(false))
            {
                if (t == null || t.font == null || IsOwnObject(t.transform)) continue;
                int len = (t.text ?? "").Length;
                if (len > bestLen) { best = t; bestLen = len; }
            }
            // The item description is the longest live text on the inventory screen.
            if (best != null && bestLen >= 40) return best.font;
        }
        catch { /* fall through to the next strategy */ }
        return null;
    }

    private static TMP_FontAsset? FindMonospaceFont()
    {
        string[] hints = { "mono", "courier", "consolas", "plex", "jetbrains", "typewriter", "code" };
        foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (f == null) continue;
            string n = (f.name ?? "").ToLowerInvariant();
            if (n.Contains("italic") || n.Contains("oblique")) continue;
            foreach (var h in hints)
                if (n.Contains(h)) return f;
        }
        return null;
    }

    private static TMP_FontAsset? FindGameFont(Transform anchor)
    {
        // Prefer an inventory heading font rather than arbitrary text in the scene.
        foreach (var sample in anchor.GetComponentsInChildren<TMP_Text>(true))
        {
            if (sample == null || sample.font == null) continue;
            string s = sample.text ?? "";
            if (s.ToUpperInvariant().Contains("INVENTORY")) return sample.font;
        }
        foreach (var sample in anchor.GetComponentsInChildren<TMP_Text>(true))
            if (sample != null && sample.font != null) return sample.font;
        foreach (var sample in Resources.FindObjectsOfTypeAll<TMP_Text>())
            if (sample != null && sample.font != null && sample.gameObject != null && sample.gameObject.activeInHierarchy)
                return sample.font;
        return null;
    }

    // ------------------------------------------------------------------------------------
    // uGUI helpers
    // ------------------------------------------------------------------------------------
    private static GameObject Make(string name, Transform parent)
    {
        var go = new GameObject(name, new Il2CppSystem.Type[] { Il2CppType.Of<RectTransform>() });
        go.transform.SetParent(parent, false);
        return go;
    }

    private static RectTransform Rect(GameObject go) => go.GetComponent<RectTransform>();

    /// <summary>Top-left anchored/pivoted rect; y is negative going down.</summary>
    private static void SetRect(GameObject go, float x, float y, float w, float h)
    {
        var rt = Rect(go);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>Fills the parent rect, inset by the same amount on every side.</summary>
    private static void Stretch(GameObject go, float inset)
    {
        var rt = Rect(go);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    private static Image AddImage(GameObject go, Color color, bool raycast)
    {
        var image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    private static TMP_Text Label(Transform parent, string name, string value,
        float x, float y, float w, float h, float size, Color color,
        TextAlignmentOptions alignment, float spacing)
    {
        var go = Make(name, parent);
        SetRect(go, x, y, w, h);
        var label = go.AddComponent<TextMeshProUGUI>();
        label.font = _font;
        label.fontSize = size;
        label.color = color;
        label.text = value;
        label.raycastTarget = false;
        label.alignment = alignment;
        label.enableWordWrapping = false;
        // Optional TMP formatting must never prevent the selector from loading.
        try { label.characterSpacing = spacing; } catch { }
        try { label.overflowMode = TextOverflowModes.Ellipsis; } catch { }
        return label;
    }

    private static Button AddClickable(GameObject go, Action handler)
    {
        var button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
        {
            try { handler(); }
            catch (Exception ex) { Plugin.Logger.LogWarning("[InventoryFilter] UI click failed: " + ex.Message); }
        })));
        return button;
    }

    internal static void Shutdown()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;
        _head = null;
        _popup = null;
        _viewport = null;

        _contentRect = null;
        _rows = Array.Empty<GameObject>();
        _headText = null;
        _popupText = null;
        _anchor = null;
        _tooltipHoverRect = null;
        _tooltipHoverCamera = null;
        _font = null;
        _open = false;
        _suppressedForTabHover = false;
        _lastSelection = "";
        // Sprites are intentionally kept (HideAndDontSave) and reused on the next Create().
    }
}
