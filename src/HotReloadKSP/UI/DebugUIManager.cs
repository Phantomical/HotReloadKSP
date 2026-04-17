using KSP.UI.Screens.DebugToolbar;
using KSP.UI.Screens.DebugToolbar.Screens;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HotReloadKSP.UI;

/// <summary>
/// Finds and caches UI prefab templates from the existing KSP debug menu screens
/// so that our custom debug screen uses the same visual theme.
/// </summary>
internal static class DebugUIManager
{
    static GameObject _labelPrefab;
    static GameObject _buttonPrefab;
    static GameObject _togglePrefab;
    static GameObject _inputFieldPrefab;
    static GameObject _spacerPrefab;
    static GameObject _scrollbarPrefab;

    static bool _initialized;

    /// <summary>
    /// Must be called after DebugScreenSpawner has been set up (e.g. from MainMenu).
    /// Searches the existing debug screen prefabs for representative UI elements and
    /// clones them as reusable templates.
    /// </summary>
    public static bool Initialize()
    {
        if (_initialized)
            return true;

        var spawner = DebugScreenSpawner.Instance;
        if (spawner == null)
        {
            Log.Warn("DebugUIManager: DebugScreenSpawner.Instance is null");
            return false;
        }

        var screens = spawner.debugScreens?.screens;
        if (screens == null)
        {
            Log.Warn("DebugUIManager: No debug screens found");
            return false;
        }

        // "Debug" (console) screen has button and input field in its BottomBar.
        // "Database" screen has labels with LayoutElement wrapper.
        // "Debugging" screen has toggles with DebugScreenToggle wrapper pattern.
        foreach (var wrapper in screens)
        {
            if (wrapper.screen == null)
                continue;

            var root = wrapper.screen.gameObject;
            switch (wrapper.name)
            {
                case "Debug":
                    FindConsolePrefabs(root);
                    break;
                case "Database":
                    FindDatabasePrefabs(root);
                    break;
                case "Debugging":
                    FindDebuggingPrefabs(root);
                    break;
            }
        }

        if (_scrollbarPrefab == null && spawner.screenPrefab != null)
        {
            var contentsScrollView = spawner.screenPrefab.transform.Find(
                "VerticalLayout/HorizontalLayout/Contents/Contents Scroll View"
            );
            var scrollbar = contentsScrollView?.Find("Scrollbar");
            if (scrollbar != null)
                _scrollbarPrefab = ClonePrefab(scrollbar.gameObject, "DebugUI_ScrollbarPrefab");
        }

        if (_spacerPrefab == null)
        {
            _spacerPrefab = new GameObject("DebugUI_SpacerPrefab", typeof(RectTransform));
            _spacerPrefab.SetActive(false);
            var layout = _spacerPrefab.AddComponent<LayoutElement>();
            layout.minHeight = 8f;
            layout.preferredHeight = 8f;
            Object.DontDestroyOnLoad(_spacerPrefab);
        }

        _initialized =
            _labelPrefab != null
            && _buttonPrefab != null
            && _togglePrefab != null
            && _inputFieldPrefab != null;

        if (!_initialized)
        {
            Log.Warn(
                $"DebugUIManager: Failed to find all prefabs. "
                    + $"label={_labelPrefab != null}, button={_buttonPrefab != null}, "
                    + $"toggle={_togglePrefab != null}, inputField={_inputFieldPrefab != null}"
            );
        }

        return _initialized;
    }

    static void FindConsolePrefabs(GameObject root)
    {
        var bottomBar = root.transform.Find("BottomBar");
        if (bottomBar == null)
            return;

        if (_inputFieldPrefab == null)
        {
            var inputFieldGo = bottomBar.Find("InputField");
            if (inputFieldGo != null)
                _inputFieldPrefab = ClonePrefab(
                    inputFieldGo.gameObject,
                    "DebugUI_InputFieldPrefab"
                );
        }

        if (_buttonPrefab == null)
        {
            var buttonGo = bottomBar.Find("Button");
            if (buttonGo != null)
                _buttonPrefab = ClonePrefab(buttonGo.gameObject, "DebugUI_ButtonPrefab");
        }
    }

    static void FindDatabasePrefabs(GameObject root)
    {
        if (_labelPrefab != null)
            return;

        var totalLabel = root.transform.Find("TotalLabel");
        if (totalLabel != null)
            _labelPrefab = ClonePrefab(totalLabel.gameObject, "DebugUI_LabelPrefab");
    }

    static void FindDebuggingPrefabs(GameObject root)
    {
        if (_togglePrefab != null)
            return;

        var toggleWrapper = root.transform.Find("PrintErrorsToScreen");
        if (toggleWrapper != null)
        {
            _togglePrefab = ClonePrefab(toggleWrapper.gameObject, "DebugUI_TogglePrefab");
            // Remove the KSP-specific behaviour script from the clone
            var debugToggle = _togglePrefab.GetComponent<DebugScreenToggle>();
            if (debugToggle != null)
                Object.DestroyImmediate(debugToggle);
        }
    }

    static GameObject ClonePrefab(GameObject source, string name)
    {
        var clone = Object.Instantiate(source);
        clone.name = name;
        clone.SetActive(false);
        Object.DontDestroyOnLoad(clone);
        return clone;
    }

    public static RectTransform CreateScreenPrefab<T>(string name)
        where T : MonoBehaviour
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.SetActive(false);
        Object.DontDestroyOnLoad(go);
        go.AddComponent<T>();

        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 4f;
        vlg.padding = new RectOffset(8, 8, 8, 8);

        return rect;
    }

    public static TextMeshProUGUI CreateLabel(Transform parent, string text)
    {
        var go = Object.Instantiate(_labelPrefab, parent, false);
        go.SetActive(true);
        go.name = "Label";

        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
        }

        // The cloned "Text" child has fixed 200px width anchored left; stretch it.
        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        var textRect = tmp.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        tmp.text = text;
        return tmp;
    }

    public static TextMeshProUGUI CreateHeader(Transform parent, string text)
    {
        var tmp = CreateLabel(parent, text);
        tmp.fontStyle = FontStyles.Bold;
        tmp.fontSize *= 1.2f;
        return tmp;
    }

    public static Button CreateButton(
        Transform parent,
        string text,
        UnityEngine.Events.UnityAction onClick
    )
    {
        var go = Object.Instantiate(_buttonPrefab, parent, false);
        go.SetActive(true);
        go.name = "Button";
        SetupButtonPrefab(go);

        var btn = go.GetComponent<Button>();
        btn.onClick.RemoveAllListeners();
        if (onClick != null)
            btn.onClick.AddListener(onClick);

        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null)
            tmp.text = text;

        return btn;
    }

    public static T CreateButton<T>(Transform parent, string text)
        where T : DebugScreenButton
    {
        var go = Object.Instantiate(_buttonPrefab, parent, false);
        go.name = "Button";
        SetupButtonPrefab(go);

        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null)
            tmp.text = text;

        var component = go.AddComponent<T>();
        component.button = go.GetComponent<Button>();

        go.SetActive(true);
        return component;
    }

    static void SetupButtonPrefab(GameObject go)
    {
        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
            layout.minHeight = 30f;
        }
    }

    public static Toggle CreateToggle(Transform parent, string label)
    {
        var go = Object.Instantiate(_togglePrefab, parent, false);
        go.name = "Toggle";

        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
        }

        StretchToggleChild(go);

        go.SetActive(true);

        var toggle = go.GetComponentInChildren<Toggle>();
        var labelTransform = toggle?.transform.Find("Label");
        if (labelTransform != null)
        {
            var tmp = labelTransform.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
                tmp.text = label;
        }

        return toggle;
    }

    /// <summary>
    /// The toggle prefab's inner "Toggle" child has a fixed width (e.g. 120px).
    /// Stretch it to fill the wrapper so the checkbox and label use the full width.
    /// </summary>
    static void StretchToggleChild(GameObject wrapper)
    {
        var innerToggle = wrapper.GetComponentInChildren<Toggle>(true);
        if (innerToggle == null)
            return;

        var rt = innerToggle.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    public static TMP_InputField CreateInputField(Transform parent)
    {
        var go = Object.Instantiate(_inputFieldPrefab, parent, false);
        go.SetActive(true);
        go.name = "InputField";

        var layout = go.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredWidth = -1;
            layout.flexibleWidth = 1;
            layout.minHeight = 30f;
        }

        var input = go.GetComponent<TMP_InputField>();
        if (input?.textComponent != null)
            input.textComponent.alignment = TextAlignmentOptions.Left;
        input.text = "";
        return input;
    }

    public static void CreateSpacer(Transform parent, float height = 8f)
    {
        var go = Object.Instantiate(_spacerPrefab, parent, false);
        go.SetActive(true);
        go.name = "Spacer";

        var layout = go.GetComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
    }

    public static GameObject CreateHorizontalLayout(Transform parent, float spacing = 8f)
    {
        var go = new GameObject("HorizontalLayout", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = spacing;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = false;

        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = 30f;
        return go;
    }

    /// <summary>
    /// Builds a vertical scroll view inside <paramref name="parent"/> and returns the
    /// inner content Transform; append list items as children of it. The parent
    /// debug ContentTransform has no ScrollRect of its own, so screens with lists
    /// must provide one here.
    /// </summary>
    public static Transform CreateScrollView(Transform parent)
    {
        var scrollGo = new GameObject("ScrollView", typeof(RectTransform));
        scrollGo.transform.SetParent(parent, false);

        var scrollLe = scrollGo.AddComponent<LayoutElement>();
        scrollLe.flexibleHeight = 1f;
        scrollLe.flexibleWidth = 1f;

        var scrollRect = scrollGo.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;

        var viewportGo = new GameObject("Viewport", typeof(RectTransform));
        viewportGo.transform.SetParent(scrollGo.transform, false);

        var viewportRect = viewportGo.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = new Vector2(-16f, 0f);

        viewportGo.AddComponent<CanvasRenderer>();
        var viewportImage = viewportGo.AddComponent<Image>();
        viewportImage.color = Color.white;
        viewportGo.AddComponent<Mask>().showMaskGraphic = false;

        scrollRect.viewport = viewportRect;

        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewportGo.transform, false);

        var contentRect = contentGo.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0f, 1f);
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
        contentVlg.childControlWidth = true;
        contentVlg.childControlHeight = true;
        contentVlg.childForceExpandWidth = true;
        contentVlg.childForceExpandHeight = false;
        contentVlg.spacing = 0f;

        var contentCsf = contentGo.AddComponent<ContentSizeFitter>();
        contentCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.content = contentRect;

        if (_scrollbarPrefab != null)
            CreateScrollbar(scrollGo.transform, scrollRect);

        return contentGo.transform;
    }

    static Scrollbar CreateScrollbar(Transform parent, ScrollRect scrollRect)
    {
        var go = Object.Instantiate(_scrollbarPrefab, parent, false);
        go.SetActive(true);
        go.name = "Scrollbar";

        var scrollbar = go.GetComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        scrollRect.verticalScrollbar = scrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect
            .ScrollbarVisibility
            .AutoHideAndExpandViewport;
        scrollRect.verticalScrollbarSpacing = 0f;

        return scrollbar;
    }
}
