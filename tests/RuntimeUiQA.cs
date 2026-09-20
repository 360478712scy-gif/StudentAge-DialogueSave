using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Test-only helpers. They dispatch through Unity's UI event interfaces and never move the OS cursor.
public static class RuntimeUiQA
{
    public static IEnumerator CheckExclusiveHover(string namedButton, Action<bool, string> check)
    {
        var root = Resources.FindObjectsOfTypeAll<Transform>().Single(t => t != null && t.gameObject.activeInHierarchy && t.name == namedButton);
        var button = root.GetComponent<Button>();
        Require(button != null && button.IsInteractable(), check, "hover target is an interactive new-save button");
        var grid = root.GetComponentsInParent<Transform>().First(t => t.name == "DialogueSave.Grid");
        var buttons = grid.GetComponentsInChildren<Button>(false).Where(b =>
            b.gameObject == button.gameObject || b.name == "btn_add").ToArray();
        Require(buttons.Length > 1 && buttons.All(b => b.targetGraphic != null), check, "hover checks multiple native slot highlight graphics");
        Require(buttons.Select(b => b.targetGraphic.GetInstanceID()).Distinct().Count() == buttons.Length, check,
            "each slot owns its own native highlight graphic");
        var rect = (RectTransform)button.transform;
        var canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
        var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var pointer = new PointerEventData(EventSystem.current) {
            position = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center)), pointerId = -1 };
        var hit = FrontHit(EventSystem.current, pointer, check, namedButton);
        Require(ExecuteEvents.GetEventHandler<IPointerEnterHandler>(hit.gameObject) == button.gameObject, check,
            "real raycast hover reaches the requested new-save button");
        Require(Mouse.current != null, check, "native InputSystem mouse exists for hover transition");
        var originalMouse = Mouse.current;
        var module = EventSystem.current.currentInputModule as InputSystemUIInputModule;
        Require(module != null && module.actionsAsset != null && module.point != null && module.point.action != null &&
            module.point.action.actionMap.asset == module.actionsAsset, check, "native UI pointer uses its action asset for scoped device isolation");
        var asset = module.actionsAsset;
        var previousDevices = asset.devices;
        Mouse mouse = null;
        var output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "results");
        Directory.CreateDirectory(output);
        var samples = new JArray { HoverSnapshot("before", buttons, pointer.position, originalMouse) };
        try
        {
            // Only this QA UI action asset is restricted. Physical devices and the OS
            // cursor stay enabled; their motion cannot overwrite the test pointer.
            mouse = InputSystem.AddDevice<Mouse>("DialogueSaveQA.HoverMouse");
            asset.devices = Keyboard.current == null ? new InputDevice[] { mouse } : new InputDevice[] { mouse, Keyboard.current };
            // The actual UI module sends exit/enter; do not create a second synthetic hover.
            InputSystem.QueueDeltaStateEvent(mouse.position, pointer.position);
            yield return new WaitForSecondsRealtime(button.colors.fadeDuration + .1f);
            yield return new WaitForEndOfFrame();
            samples.Add(HoverSnapshot("entered", buttons, pointer.position, mouse));
            File.WriteAllText(Path.Combine(output, "hover-diagnostics.json"), samples.ToString());
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "hover-entered.png"));
            yield return new WaitForSecondsRealtime(.2f);
            Require(Vector2.Distance(module.point.action.ReadValue<Vector2>(), pointer.position) < .1f, check,
                "native UI pointer stays at isolated QA hover position");
            Require(button.targetGraphic.canvasRenderer.GetColor().a > .9f &&
                buttons.Where(b => b != button).All(b => b.targetGraphic.canvasRenderer.GetColor().a < .1f), check,
                "only the hovered new-save slot shows the yellow native highlight");
            InputSystem.QueueDeltaStateEvent(mouse.position, new Vector2(1f, 1f));
            yield return new WaitForSecondsRealtime(button.colors.fadeDuration + .1f);
            yield return new WaitForEndOfFrame();
            samples.Add(HoverSnapshot("exited", buttons, pointer.position, mouse));
            File.WriteAllText(Path.Combine(output, "hover-diagnostics.json"), samples.ToString());
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "hover-exited.png"));
            yield return new WaitForSecondsRealtime(.2f);
            Require(buttons.All(b => b.targetGraphic.canvasRenderer.GetColor().a < .1f), check,
                "leaving the new-save slot clears its native highlight");
        }
        finally
        {
            try { if (asset != null) asset.devices = previousDevices; }
            finally
            {
                if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
                if (originalMouse != null && originalMouse.added) originalMouse.MakeCurrent();
            }
        }
    }

    private static JObject HoverSnapshot(string phase, Button[] buttons, Vector2 testPosition, Mouse sourceMouse)
    {
        Func<Color, JArray> color = c => new JArray(c.r, c.g, c.b, c.a);
        Func<Button, string, JToken> state = (b, name) => {
            var field = typeof(Selectable).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? JValue.CreateNull() : JToken.FromObject(field.GetValue(b)); };
        var mouse = sourceMouse.position.ReadValue();
        var module = EventSystem.current.currentInputModule as InputSystemUIInputModule;
        var uiPoint = module == null || module.point == null ? Vector2.zero : module.point.action.ReadValue<Vector2>();
        return new JObject {
            ["phase"] = phase, ["testPosition"] = new JArray(testPosition.x, testPosition.y),
            ["mousePosition"] = new JArray(mouse.x, mouse.y), ["timeScale"] = Time.timeScale,
            ["mouseDevice"] = sourceMouse.name, ["mouseDeviceId"] = sourceMouse.deviceId,
            ["uiPoint"] = new JArray(uiPoint.x, uiPoint.y),
            ["uiAllowedDevices"] = module == null || module.actionsAsset == null || !module.actionsAsset.devices.HasValue ? null :
                new JArray(module.actionsAsset.devices.Value.Select(d => d.name + ":" + d.deviceId)),
            ["inputModule"] = EventSystem.current.currentInputModule == null ? null : EventSystem.current.currentInputModule.GetType().FullName,
            ["selected"] = EventSystem.current.currentSelectedGameObject == null ? null : ObjectPath(EventSystem.current.currentSelectedGameObject.transform),
            ["buttons"] = new JArray(buttons.Select(b => new JObject {
                ["path"] = ObjectPath(b.transform), ["interactable"] = b.IsInteractable(), ["transition"] = b.transition.ToString(),
                ["pointerInside"] = state(b, "m_IsPointerInside"), ["pointerDown"] = state(b, "m_IsPointerDown"),
                ["hasSelection"] = state(b, "m_HasSelection"), ["targetGraphicId"] = b.targetGraphic.GetInstanceID(),
                ["targetGraphicPath"] = ObjectPath(b.targetGraphic.transform), ["graphicColor"] = color(b.targetGraphic.color),
                ["rendererColor"] = color(b.targetGraphic.canvasRenderer.GetColor()), ["normalColor"] = color(b.colors.normalColor),
                ["highlightColor"] = color(b.colors.highlightedColor), ["selectedColor"] = color(b.colors.selectedColor),
                ["disabledColor"] = color(b.colors.disabledColor) })) };
    }

    public static JObject PointerReport(GameObject target)
    {
        var rect = (RectTransform)target.transform;
        var canvas = rect.GetComponentInParent<Canvas>();
        var rootCanvas = canvas.rootCanvas;
        var camera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
        var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        var ancestors = new JArray();
        for (Transform t = rect; t != null; t = t.parent)
        {
            var c = t.GetComponent<Canvas>();
            var r = t.GetComponent<GraphicRaycaster>();
            ancestors.Add(new JObject
            {
                ["path"] = ObjectPath(t), ["active"] = t.gameObject.activeInHierarchy,
                ["canvas"] = c == null ? null : new JObject { ["enabled"] = c.enabled, ["overrideSorting"] = c.overrideSorting,
                    ["sortingOrder"] = c.sortingOrder, ["renderOrder"] = c.renderOrder },
                ["raycaster"] = r == null ? null : new JObject { ["enabled"] = r.enabled,
                    ["sortPriority"] = r.sortOrderPriority, ["renderPriority"] = r.renderOrderPriority },
                ["groups"] = new JArray(t.GetComponents<CanvasGroup>().Select(g => new JObject {
                    ["enabled"] = g.enabled, ["interactable"] = g.interactable, ["blocksRaycasts"] = g.blocksRaycasts,
                    ["ignoreParentGroups"] = g.ignoreParentGroups, ["alpha"] = g.alpha }))
            });
        }
        return new JObject
        {
            ["target"] = ObjectPath(rect), ["point"] = new JArray(point.x, point.y),
            ["localRect"] = rect.rect.ToString(), ["ancestors"] = ancestors,
            ["graphics"] = new JArray(target.GetComponentsInChildren<Graphic>(true).Select(g => new JObject {
                ["path"] = ObjectPath(g.transform), ["type"] = g.GetType().FullName,
                ["active"] = g.gameObject.activeInHierarchy, ["enabled"] = g.enabled, ["raycastTarget"] = g.raycastTarget,
                ["depth"] = g.depth, ["culled"] = g.canvasRenderer.cull, ["raycastAccepted"] = g.Raycast(point, camera),
                ["containsPoint"] = RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, point, camera),
                ["canvas"] = g.canvas == null ? null : ObjectPath(g.canvas.transform), ["rect"] = g.rectTransform.rect.ToString() })),
            ["hits"] = new JArray(hits.Select(h => new JObject { ["path"] = ObjectPath(h.gameObject.transform),
                ["module"] = h.module.GetType().FullName, ["depth"] = h.depth, ["sortingOrder"] = h.sortingOrder,
                ["sortingLayer"] = h.sortingLayer, ["distance"] = h.distance }))
        };
    }

    public static void Click(string namedRoot, Action<bool, string> check, Vector2? normalizedPoint = null)
    {
        Require(check != null, null, "QA check callback is required");
        Require(!string.IsNullOrEmpty(namedRoot), check, "click has an explicit object name");
        var roots = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t != null && t.gameObject.activeInHierarchy && t.name == namedRoot).ToArray();
        Require(roots.Length == 1, check, "exactly one active click root: " + namedRoot + " (found " + roots.Length + ")");
        Transform root = roots[0];
        var candidates = root.GetComponentsInChildren<Button>(false)
            .Where(b => b != null && b.isActiveAndEnabled && b.gameObject.activeInHierarchy)
            .OrderBy(b => Depth(b.transform, root)).ToArray();
        Require(candidates.Length > 0, check, "active Button exists under " + namedRoot);
        int minimumDepth = Depth(candidates[0].transform, root);
        var primary = candidates.Where(b => Depth(b.transform, root) == minimumDepth).ToArray();
        Require(primary.Length == 1, check, "unambiguous primary Button under " + namedRoot);
        Button button = primary[0];
        Require(button.IsInteractable(), check, "Button and parent CanvasGroups allow interaction: " + namedRoot);
        var rect = button.transform as RectTransform;
        Require(rect != null && rect.rect.width > 0 && rect.rect.height > 0, check, "Button rectangle is laid out: " + namedRoot);

        var events = EventSystem.current;
        Require(events != null && events.isActiveAndEnabled, check, "active EventSystem for " + namedRoot);
        var canvas = rect.GetComponentInParent<Canvas>();
        Require(canvas != null, check, "Button belongs to a Canvas: " + namedRoot);
        var rootCanvas = canvas.rootCanvas;
        Camera camera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
        Require(rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay || camera != null, check,
            "Canvas camera is available: " + namedRoot);
        Vector2 localPoint=normalizedPoint.HasValue
            ? new Vector2(rect.rect.xMin+rect.rect.width*normalizedPoint.Value.x,rect.rect.yMin+rect.rect.height*normalizedPoint.Value.y)
            : rect.rect.center;
        Vector2 center = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(localPoint));
        Require(Finite(center.x) && Finite(center.y) && center.x >= 0 && center.y >= 0 &&
            center.x <= Screen.width && center.y <= Screen.height, check, "Button center is on screen: " + namedRoot);

        var pointer = new PointerEventData(events)
        {
            pointerId = -1,
            button = PointerEventData.InputButton.Left,
            position = center,
            pressPosition = center,
            delta = Vector2.zero,
            clickCount = 1,
            clickTime = Time.unscaledTime,
            eligibleForClick = true,
            useDragThreshold = false
        };
        RaycastResult hit = FrontHit(events, pointer, check, namedRoot);
        var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
        Require(clickTarget == button.gameObject, check,
            "front raycast resolves to requested Button: " + namedRoot + " (front " + ObjectPath(hit.gameObject.transform) + ")");
        pointer.pointerCurrentRaycast = hit;
        pointer.pointerPressRaycast = hit;
        pointer.rawPointerPress = hit.gameObject;
        pointer.pointerEnter = hit.gameObject;

        GameObject pressed = null;
        bool released = false;
        try
        {
            pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            pointer.pointerPress = pressed;
            Require(pressed == button.gameObject, check, "pointerDown reaches requested Button: " + namedRoot);
            Require(ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerUpHandler), check,
                "pointerUp reaches pressed Button: " + namedRoot);
            released = true;

            var releaseHit = FrontHit(events, pointer, check, namedRoot);
            var releaseTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(releaseHit.gameObject);
            Require(releaseTarget == pressed && button != null && button.isActiveAndEnabled && button.IsInteractable(),
                check, "release still targets the same interactive Button: " + namedRoot);
            pointer.pointerCurrentRaycast = releaseHit;
            Require(ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerClickHandler), check,
                "pointerClick dispatched through EventSystem handler: " + namedRoot);
        }
        finally
        {
            if (!released && pressed != null) ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerUpHandler);
            pointer.eligibleForClick = false;
            pointer.pointerPress = null;
            pointer.rawPointerPress = null;
        }
    }

    // Call after WaitForEndOfFrame. Every dialogue context now exposes the same four request actions;
    // asynchronous capture acceptance and eventual disk completion are verified separately.
    public static void CheckLayout(DialogueUiController ui, Action<bool, string> check)
    {
        Require(ui != null && check != null, check, "layout checker has controller and callback");
        var report = JObject.Parse(ui.CaptureLayoutReport());
        var windows = report["saveWindows"] as JArray;
        var actionRows = report["dialogueActionRows"] as JArray;
        var menus = report["escapeMenus"] as JArray ?? new JArray();
        Require(windows != null && actionRows != null, check, "layout report contains window and toolbar collections");
        Require((int?)report["screenWidth"] > 0 && (int?)report["screenHeight"] > 0, check, "layout report has screen dimensions");
        int observedRows = actionRows.Count(row => (row["labels"] as JArray)?.Count > 0);
        Require(windows.Count > 0 || observedRows > 0 || menus.Count > 0, check, "layout checks observed an actual save window, dialogue toolbar or pause menu");
        if (observedRows > 0)
        {
            Require((int?)report["visibleToolbarCount"] == 1, check, "dialogue has exactly one native toolbar owner");
            var repeated = report["globalRepeatedCaptions"] as JArray ?? new JArray();
            foreach (var text in new[] { "日志", "自动", "菜单" })
                Require(repeated.Count(c => (string)c["text"] == text) <= 1, check,
                    "native toolbar caption is not duplicated across TopView and HotkeyView: " + text);
        }

        int windowIndex = 0;
        foreach (var window in windows)
        {
            string tag = "save window " + windowIndex++;
            True(window, "nativeTabCountUnchanged", check, tag);
            bool saving = RequiredBool(window, "saving");
            if (!saving) True(window, "nativeTabsVisible", check, tag);
            bool dialogue = RequiredBool(window, "dialoguePageOpen");
            var tabs = window["nativeTabs"] as JArray;
            Require(tabs != null && tabs.Count == (saving ? 1 : 3), check, tag + " retains original tab count");
            string[] expected = saving ? new[] { "手动存档" } : new[] { "手动存档", "自动存档", "快速存档" };
            for (int i = 0; i < tabs.Count; i++)
            {
                Require((int?)tabs[i]["id"] == i, check, tag + " preserves native tab id " + i);
                Require((string)tabs[i]["normalLabel"] == expected[i] && (string)tabs[i]["selectedLabel"] == expected[i],
                    check, tag + " preserves native tab labels " + i);
                CheckVisibleRect(tabs[i]["rect"], check, tag + " native tab " + i);
            }
            Require((string)window["entryLabel"] == "对话存档" && (string)window["entrySelectedLabel"] == "对话存档",
                check, tag + " keeps dialogue entry identity in both states");
            {
                CheckVisibleRect(window["entry"], check, tag + " independent dialogue entry");
                var entryBounds = window["entry"]["screenBounds"];
                var firstBounds = tabs[0]["rect"]["screenBounds"];
                Require((float)entryBounds["x"] + (float)entryBounds["width"] <= (float)firstBounds["x"] + 1f,
                    check, tag + " dialogue entry remains left of manual tab");
                CheckRenderedTab(window["firstNativeTabGraphics"] as JArray, check, tag + " reference native tab");
                CheckRenderedTab(window["entryGraphics"] as JArray, check, tag + " dialogue entry");
            }
            if (!dialogue) continue;

            True(window, "originalGridHidden", check, tag);
            True(window, "originalTabsVisible", check, tag);
            True(window, "hierarchyDoesNotOverlap", check, tag);
            True(window, "cardsDoNotOverlapFooter", check, tag);
            True(window, "visibleCardsAtMostNine", check, tag);
            True(window, "cardsDoNotOverlap", check, tag);
            True(window, "cardsInsideGrid", check, tag);
            CheckVisibleRect(window["grid"], check, tag + " dialogue grid");
            var secondary = window["secondaryTabs"] as JArray;
            Require(secondary != null && secondary.Count == (saving ? 0 : 3), check,
                tag + " has separate dialogue categories only in loading mode");
            for (int i = 0; i < secondary.Count; i++)
            {
                Require((string)secondary[i]["label"] == expected[i], check, tag + " dialogue category " + i);
                CheckVisibleRect(secondary[i]["rect"], check, tag + " dialogue category " + i);
                CheckRenderedTab(secondary[i]["graphics"] as JArray, check, tag + " dialogue category " + i);
                var lower = secondary[i]["rect"]["screenBounds"];
                var upper = tabs[i]["rect"]["screenBounds"];
                Require((float)lower["y"] + (float)lower["height"] <= (float)upper["y"] + 1f,
                    check, tag + " dialogue category is below the unchanged native tabs");
            }
            var cards = window["cards"] as JArray;
            Require(cards != null && cards.Count == 9, check, tag + " owns exactly nine native card templates");
            foreach (var card in cards)
            {
                if ((bool?)card["rect"]?["activeInHierarchy"] != true) continue;
                string cardTag = tag + " card " + (int?)card["index"];
                CheckVisibleRect(card["rect"], check, cardTag);
                True(card, "sameNativeCardSize", check, cardTag);
                True(card, "sameSpeakerFont", check, cardTag);
                True(card, "sameSummaryFont", check, cardTag);
                JToken gradient = card["matchesSeasonConfig"];
                if (gradient != null && gradient.Type != JTokenType.Null)
                    Require(gradient.Type == JTokenType.Boolean && (bool)gradient, check, cardTag + " uses native season gradient");
                else
                    Require(card["seasonId"] == null || card["seasonId"].Type == JTokenType.Null, check,
                        cardTag + " does not silently omit a known season gradient");
            }
        }

        int rowIndex = 0;
        foreach (var row in actionRows)
        {
            var labels = row["labels"] as JArray;
            if (labels == null || labels.Count == 0) continue;
            string tag = "dialogue toolbar " + rowIndex++;
            True(row, "labelsUnique", check, tag);
            True(row, "noOverlap", check, tag);
            True(row, "allCellsDoNotOverlap", check, tag);
            True(row, "nativePositionsUnchanged", check, tag + " preserves original Log/Auto/Menu coordinates");
            foreach (var rect in (JArray)row["allCellRectangles"]) CheckVisibleRect(rect, check, tag + " native and plugin cell");
            var actual = labels.Select(x => (string)x).ToArray();
            Require(actual.SequenceEqual(new[] { "保存", "加载", "快速保存", "快速加载" }), check,
                tag + " contains four actions exactly once in order");
            var rectangles = row["rectangles"] as JArray;
            Require(rectangles != null && rectangles.Count == 4, check, tag + " has four laid-out action rectangles");
            foreach (var rect in rectangles) CheckVisibleRect(rect, check, tag + " " + (string)rect["name"]);
            var graphics = row["graphics"] as JArray;
            var bindings = row["bindings"] as JArray;
            Require(graphics != null && graphics.Count == 4 && bindings != null && bindings.Count == 4,
                check, tag + " has key-cap diagnostics for all four actions");
            for (int i = 0; i < 4; i++) CheckKeycap(graphics[i]["items"] as JArray, bindings[i], check, tag + " " + actual[i]);
        }
        foreach (var menu in menus)
        {
            True(menu, "dialoguePauseMenu", check, "pause menu");
            True(menu, "noOverlap", check, "pause menu");
            True(menu, "groupGeometryUnchanged", check, "pause menu preserves the original group transform and layout");
            var buttons = menu["buttons"] as JArray;
            Require(buttons != null && buttons.Count == 2, check,
                "pause menu exposes only native save and load dialogue actions");
            True(menu, "hasNoKeyCaps", check, "pause menu");
            string[] actionNames = { "Save", "Load" };
            for (int i = 0; i < 2; i++)
            {
                string name = "DialogueSave.Menu." + actionNames[i];
                Require((string)buttons[i]["name"] == name, check, "pause menu action order: " + name);
                True(buttons[i], "sameNativeSize", check, name);
                CheckVisibleRect(buttons[i]["rect"], check, name);
            }
            var allButtons = menu["allButtons"] as JArray;
            Require(allButtons != null && allButtons.Count == 5, check, "pause menu retains five rows in the unchanged native menu group");
            foreach (var rect in allButtons) CheckVisibleRect(rect, check, "pause menu complete row");
        }
    }

    private static void CheckKeycap(JArray graphics, JToken binding, Action<bool, string> check, string label)
    {
        Require(graphics != null && binding != null && !string.IsNullOrEmpty((string)binding["key"]), check,
            label + " declares its actual selected key");
        var drawable = graphics.Where(g => (bool?)g["active"] == true && (bool?)g["enabled"] == true &&
            (bool?)g["culled"] == false && (float?)g["rendererAlpha"] > 0 && (float?)g["color"]?[3] > 0).ToArray();
        var keycap = drawable.FirstOrDefault(g => (string)g["name"] == "icon_item" && (string)g["type"] == "Image" &&
            !string.IsNullOrEmpty((string)g["sprite"]));
        Require(keycap != null, check, label + " has a visible native key-cap sprite");
        CheckVisibleRect(keycap["rect"], check, label + " key cap");
        if ((bool?)binding["renderLabel"] == true)
        {
            var letter = drawable.FirstOrDefault(g => (string)g["name"] == "DialogueSave.KeyLabel" &&
                (string)g["text"] == (string)binding["key"] && !string.IsNullOrEmpty((string)g["font"]));
            Require(letter != null, check, label + " key-cap letter matches the dispatched key");
            var font = (string)letter["font"];
            Require(font == "LiberationSans SDF" || font == "SourceHanSansCN-Regular SDF" || font == "Arial", check,
                label + " key-cap letter uses a sans face matching native keycaps");
            CheckVisibleRect(letter["rect"], check, label + " key-cap letter");
        }
    }

    private static RaycastResult FrontHit(EventSystem events, PointerEventData pointer, Action<bool, string> check, string name)
    {
        var hits = new List<RaycastResult>();
        events.RaycastAll(pointer, hits);
        Require(hits.Count > 0 && hits[0].gameObject != null, check, "UI raycast has a frontmost hit: " + name);
        return hits[0];
    }

    private static void CheckVisibleRect(JToken rect, Action<bool, string> check, string label)
    {
        Require(rect != null && rect.Type == JTokenType.Object && (bool?)rect["missing"] != true, check, label + " rectangle exists");
        True(rect, "activeInHierarchy", check, label);
        True(rect, "insideScreen", check, label);
        Require((float?)rect["width"] > 0 && (float?)rect["height"] > 0, check, label + " has positive native dimensions");
    }

    private static void CheckRenderedTab(JArray graphics, Action<bool, string> check, string label)
    {
        Require(graphics != null, check, label + " has renderer diagnostics");
        var drawable = graphics.Where(g => (bool?)g["active"] == true && (bool?)g["enabled"] == true &&
            (bool?)g["culled"] == false && (float?)g["rendererAlpha"] > 0 &&
            (float?)g["color"]?[3] > 0 && (float?)g["rect"]?["width"] > 0 && (float?)g["rect"]?["height"] > 0).ToArray();
        Require(drawable.Any(g => (string)g["type"] == "Image" && !string.IsNullOrEmpty((string)g["sprite"])), check,
            label + " has a drawable sprite background, not merely an empty root rectangle");
        Require(drawable.Any(g => (string)g["type"] == "Text" && !string.IsNullOrEmpty((string)g["font"])), check,
            label + " has a drawable label font");
    }

    private static bool RequiredBool(JToken token, string key)
    {
        var value = token?[key];
        if (value == null || value.Type != JTokenType.Boolean) throw new InvalidOperationException("Layout report lacks boolean " + key);
        return (bool)value;
    }

    private static void True(JToken token, string key, Action<bool, string> check, string label)
    {
        Require(RequiredBool(token, key), check, label + " " + key);
    }

    private static void Require(bool pass, Action<bool, string> check, string message)
    {
        if (check != null) check(pass, message);
        if (!pass) throw new InvalidOperationException("Runtime UI QA failed: " + message);
    }

    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

    private static int Depth(Transform child, Transform root)
    {
        int depth = 0;
        while (child != null && child != root) { depth++; child = child.parent; }
        return child == root ? depth : int.MaxValue;
    }

    private static string ObjectPath(Transform obj)
    {
        var names = new List<string>();
        while (obj != null) { names.Add(obj.name); obj = obj.parent; }
        names.Reverse();
        return string.Join("/", names.ToArray());
    }
}
