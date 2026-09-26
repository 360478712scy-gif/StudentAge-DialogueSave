using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Components;
using StudentAgeDialogueSave.GameIntegration;
using TMPro;
using GenUI.Main;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Main;
using View.Common;

namespace StudentAgeDialogueSave.UI
{
    public sealed class DialogueUiController : IDisposable
    {
        private static DialogueUiController active;
        private static readonly FieldInfo UiInstanceField = AccessTools.Field(typeof(UIMgr), "ins");
        private static readonly FieldInfo UiViewsField = AccessTools.Field(typeof(UIMgr), "viewDict");
        internal static bool IsUiReady()
        {
            var instance = UiInstanceField == null ? null : UiInstanceField.GetValue(null);
            return instance != null && UiViewsField != null && UiViewsField.GetValue(instance) != null;
        }
        private readonly IDialogueUiService service;
        private readonly Action<string> log;
        private readonly Dictionary<SaveView,bool> loadingDefaults=new Dictionary<SaveView,bool>();
        private readonly HashSet<SaveView> originalArchives = new HashSet<SaveView>();
        private readonly Dictionary<SaveView, AdvSavePage> advPages = new Dictionary<SaveView, AdvSavePage>();
        private readonly Dictionary<SaveView, DialogueSavePage> pages = new Dictionary<SaveView, DialogueSavePage>();
        private readonly Dictionary<BaseView, HotkeyDecoration> hotkeys = new Dictionary<BaseView, HotkeyDecoration>();
        private readonly List<Tuple<MethodBase, MethodInfo>> patches = new List<Tuple<MethodBase, MethodInfo>>();
        private Harmony harmony;
        private bool disposed;
        private bool pendingMenu;
        private float pendingSince;
        private float nextReconcile;
        private DialogueHotkeyBindings bindings;
        internal string KeyLabel(DialogueHotkeyAction action)=>bindings?.GetBinding(action)?.Label??"";
        private readonly Dictionary<EntryView, DialogueEscapeMenu> escapeMenus = new Dictionary<EntryView, DialogueEscapeMenu>();
        private string lastActionDiagnostic;
        private int actionDiagnosticCount;
        private bool refreshingActions, decoratingHotkeys, decoratingMenu;
        private readonly Dictionary<GameObject, bool> suppressedRows = new Dictionary<GameObject, bool>();
        private static readonly MethodInfo TopRefreshHotkey = AccessTools.Method(typeof(TopView), "RefreshHotkey");

        private sealed class HotkeyDecoration
        {
            internal readonly List<GameObject> Added = new List<GameObject>();
            internal readonly Dictionary<GameObject, JObject> NativeBaseline = new Dictionary<GameObject, JObject>();
            internal readonly Dictionary<GameObject, bool> Hidden = new Dictionary<GameObject, bool>();
            internal RectTransform ForcedGroup;
            internal bool OriginalGroupActive;
            internal void Clear()
            {
                foreach (var obj in Added) NativeUiCloner.Remove(obj);
                Added.Clear(); NativeBaseline.Clear();
                foreach (var pair in Hidden) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                Hidden.Clear();
                if (ForcedGroup != null)
                {
                    ForcedGroup.gameObject.SetActive(OriginalGroupActive);
                    ForcedGroup = null;
                }
            }
        }

        public DialogueUiController(IDialogueUiService service, Action<string> log)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.log = log ?? (_ => { });
        }

        public void Install(Harmony owner)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DialogueUiController));
            if (harmony != null) throw new InvalidOperationException("对话 UI 已挂接。");
            if (active != null) throw new InvalidOperationException("另一对话 UI 控制器已挂接。");
            harmony = owner ?? throw new ArgumentNullException(nameof(owner));
            active = this;
            try
            {
                var constructor=AccessTools.Constructor(typeof(SaveView));var constructorPatch=AccessTools.Method(typeof(DialogueUiController),nameof(ArchiveConstructed));
                harmony.Patch(constructor,postfix:new HarmonyMethod(constructorPatch));patches.Add(Tuple.Create((MethodBase)constructor,constructorPatch));
                Patch(typeof(UIMgr),"GetView",nameof(ArchiveRetrieved),false,new[]{typeof(string)});
                Patch(typeof(SaveView), "OnOpen", nameof(SaveOpened), false);
                Patch(typeof(SaveView), "Refresh", nameof(SaveRefreshed), false);
                Patch(typeof(SaveView), "Refresh", nameof(SaveRefreshPrefix), true);
                Patch(typeof(UIToggleGroup), "Select", nameof(NativeTabSelected), true, new[] { typeof(object), typeof(UICell) });
                Patch(typeof(HotkeyView), "Refresh", nameof(HotkeysBeforeRefresh), true);
                Patch(typeof(HotkeyView), "Refresh", nameof(HotkeysRefreshed), false);
                Patch(typeof(TopView), "Refresh", nameof(HotkeysBeforeRefresh), true);
                Patch(typeof(TopView), "Refresh", nameof(HotkeysRefreshed), false);
                Patch(typeof(TopView), "RefreshHotkey", nameof(HotkeysBeforeRefresh), true);
                Patch(typeof(TopView), "RefreshHotkey", nameof(HotkeysRefreshed), false);
                Patch(typeof(NewTalkView), "OnHotKeyInput", nameof(DialogueHotkey), true);
                Patch(typeof(NewTalkView), "OnOpen", nameof(DialogueOpened), false);
                Patch(typeof(EntryView), "Refresh", nameof(EntryRefreshed), false);
                Patch(typeof(EntryView), "SaveGame", nameof(EntrySavePrefix), true);
                Patch(typeof(EntryView), "OnHotKeyInput", nameof(EntryHotkey), true);
                Patch(typeof(BaseView), "OnClose", nameof(ViewClosed), false);
                Patch(typeof(BaseView), "OnDestroy", nameof(ViewClosed), true);
                bindings = new DialogueHotkeyBindings(() => !disposed && service.IsDialogueContext && EventControls && AdvDialogueController.Active?.BlocksActions != true,
                    ExecuteAction, log);
                bindings.BindingsChanged += RefreshActions;
                RefreshActions();
                log("原生对话 UI 挂接完成；尚不代表视觉验收通过。");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Open(bool saving)
        {
            if (disposed) return;
            // Do not restart the menu transaction on a repeated toolbar click: BeginMenu
            // owns the capture generation and restarting it would cancel an accepted save.
            if (pendingMenu || advPages.Count>0 || pages.Values.Any(x => x.IsEntered || x.OwnsMenuLease))
            {
                return;
            }
            string reason;
            if (!service.BeginMenu(AdvDialogueController.Active?.IsAdv==true ? false : saving, out reason)) { service.Notify(reason); return; }
            pendingMenu = true;
            pendingSince = Time.realtimeSinceStartup;
            try
            {
                // NewTalkView's transparent advance surface owns a raised child Canvas.
                // Use the native Tips layer for the entire window and its mask: it is
                // above dialogue input, while later CommonComfirmView dialogs (also Tips)
                // retain their native sibling order above this window.
                UIMgr.OpenView<SaveView>(service.IsDialogueContext ? UILayerType.Tips : UILayerType.None, () =>
                {
                    if (disposed || !pendingMenu) return;
                    var view = UIMgr.GetView<SaveView>() as SaveView;
                    if (view != null) Attach(view);
                }, new object[] { saving });
            }
            catch (Exception ex)
            {
                CancelPending();
                log("打开对话存档界面失败：" + ex);
                service.Notify("存档窗口未能打开，已返回当前对话。");
            }
        }

        // The persistent host calls this cheaply; no resource scan or UI rebuild occurs every frame.
        public void Tick()
        {
            if (disposed || !IsUiReady()) return;
            bindings?.Tick();
            if (Time.realtimeSinceStartup >= nextReconcile)
            {
                nextReconcile = Time.realtimeSinceStartup + .25f;
                ReconcileActions();
            }
            if (!disposed && pendingMenu && Time.realtimeSinceStartup - pendingSince > 30f)
            {
                CancelPending();
                service.Notify("存档窗口加载超时，已解除对话暂停。");
            }
        }

        internal void SuspendDialogueControls()
        {
            foreach(var view in hotkeys.Keys.ToArray())ClearHotkeys(view);
        }

        public void RefreshRecords()
        {
            if (disposed) return;
            foreach (var page in pages.Values.ToArray()) Guard(page.RefreshRecords);
            foreach (var page in advPages.Values.ToArray()) Guard(page.RefreshRecords);
        }

        private static UIItemGroup ToolbarGroup(BaseView view)
        {
            var top = view as TopView;
            if (top != null) return top.itemgroup_key;
            var keys = view as HotkeyView;
            return keys == null ? null : keys.itemgroup_key;
        }
        private static Cell_KeyItemUI ToolbarTemplate(BaseView view)
        {
            var top = view as TopView;
            if (top != null) return top.Cell_KeyItem;
            return ((HotkeyView)view).Cell_KeyItem;
        }
        private static BaseView PrimaryToolbar()
        {
            var top = UIMgr.GetView<TopView>(false) as TopView;
            if (top != null && top.viewState == ViewState.Opened && top.gameObject != null &&
                top.gameObject.activeInHierarchy && top.itemgroup_key != null) return top;
            return UIMgr.GetView<HotkeyView>(false);
        }
        internal int ToolbarRefreshCount {get;private set;}
        bool EventControls
        {
            get {var view=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                return view!=null && !DialoguePresentationPolicy.IsTransition(view) && !AdvDialogueController.IsComic(view);}
        }
        bool NativeControls=>EventControls && AdvDialogueController.Active?.UsesAdv(UIMgr.GetView<NewTalkView>(false) as NewTalkView)!=true;
        private void RefreshToolbar(BaseView view)
        {
            ToolbarRefreshCount++;
            if (view is TopView) TopRefreshHotkey.Invoke(view, null);
            else view.Refresh();
        }
        private void UpdateDuplicateRows()
        {
            var primary = PrimaryToolbar();
            var legacy = UIMgr.GetView<HotkeyView>(false) as HotkeyView;
            bool suppress = service.IsDialogueContext && EventControls && primary is TopView && legacy != null && legacy.itemgroup_key != null;
            if (suppress)
            {
                ClearHotkeys(legacy);
                var obj = legacy.itemgroup_key.gameObject;
                if (!suppressedRows.ContainsKey(obj)) suppressedRows[obj] = obj.activeSelf;
                obj.SetActive(false);
            }
            foreach (var pair in suppressedRows.ToArray())
            {
                if (suppress && pair.Key == legacy.itemgroup_key.gameObject) continue;
                suppressedRows.Remove(pair.Key);
                if (pair.Key != null) pair.Key.SetActive(pair.Value);
            }
        }

        // Discover the native view even if its first Refresh ran before dialogue readiness.
        public void RefreshActions()
        {
            if (disposed || !IsUiReady() || refreshingActions || decoratingHotkeys || decoratingMenu) return;
            refreshingActions = true;
            try
            {
                var view = PrimaryToolbar();
                if (view != null && ToolbarGroup(view) != null && view.gameObject != null)
                    Guard(() => RefreshToolbar(view));
                var entry = UIMgr.GetView<EntryView>(false) as EntryView;
                if (entry != null) Guard(() => DecorateEntry(entry));
            }
            finally { refreshingActions = false; }
        }

        private void ReconcileActions()
        {
            if (!IsUiReady()) return;
            UpdateDuplicateRows();
            // Covers native menu/title entries and an already-open save view
            // after switching styles; use the registry, never a scene scan.
            if(AdvDialogueController.Active?.IsAdv==true)
            {
                var openSave=UIMgr.GetView<SaveView>(false) as SaveView;
                if(openSave!=null && openSave.isViewReady && openSave.gameObject.activeInHierarchy && !advPages.ContainsKey(openSave) && !originalArchives.Contains(openSave))Guard(()=>Attach(openSave));
            }
            var view = PrimaryToolbar();
            var top = UIMgr.GetTopView(ViewType.Guide, ViewType.Side);
            bool expected = service.IsDialogueContext && top is NewTalkView && NativeControls;
            HotkeyDecoration decoration;
            int activeCount = view != null && hotkeys.TryGetValue(view, out decoration) ?
                decoration.Added.Count(o => o != null && o.activeSelf) : 0;
            string state = "context=" + service.IsDialogueContext + ";top=" + (top == null ? "none" : top.GetType().Name) +
                ";toolbar=" + (view != null) + ";actions=" + activeCount;
            if (state != lastActionDiagnostic)
            {
                lastActionDiagnostic = state;
                if (actionDiagnosticCount++ < 30) log("对话操作栏状态：" + state);
            }
            if (view != null && ToolbarGroup(view) != null && ((expected && activeCount != 4) || (!expected && activeCount != 0)))
                Guard(() => RefreshToolbar(view));
            var entry = top as EntryView;
            if (entry != null && !escapeMenus.ContainsKey(entry)) Guard(() => DecorateEntry(entry));
        }

        public void ExecuteAction(DialogueHotkeyAction action)
        {
            if (disposed || !IsUiReady() || !service.IsDialogueContext || AdvDialogueController.Active?.BlocksActions == true) return;
            var entry = UIMgr.GetTopView(ViewType.Guide, ViewType.Side) as EntryView;
            if (entry != null)
            {
                if (!DialogueEscapeMenu.IsDialogueMenu(entry)) return;
                // Native CloseView removes this transient view from UIMgr synchronously.
                // Dispatch in the same stack, before any countdown/automatic playback frame can run.
                UIMgr.CloseView(entry);
            }
            switch (action)
            {
                case DialogueHotkeyAction.Save: Open(true); break;
                case DialogueHotkeyAction.Load: Open(false); break;
                case DialogueHotkeyAction.QuickSave: service.QuickSave(); break;
                case DialogueHotkeyAction.QuickLoad: service.QuickLoad(); break;
            }
        }

        private void DecorateEntry(EntryView entry)
        {
            if (decoratingMenu) return;
            decoratingMenu = true;
            try { DecorateEntryCore(entry); }
            finally { decoratingMenu = false; }
        }
        private void DecorateEntryCore(EntryView entry)
        {
            DialogueEscapeMenu existing;
            if (escapeMenus.TryGetValue(entry, out existing))
            {
                existing.Dispose(); escapeMenus.Remove(entry);
            }
            if (!service.IsDialogueContext || !EventControls || !DialogueEscapeMenu.IsDialogueMenu(entry) || bindings == null) return;
            escapeMenus.Add(entry, new DialogueEscapeMenu(entry, a => Guard(() => ExecuteAction(a))));
        }

        // QA calls this on the main thread after layout has settled, and chooses its own output path.
        // It records evidence; an empty report must never count as a passed visual test.
        public string CaptureLayoutReport()
        {
            var pageReports = new JArray();
            foreach (var pair in pages)
                if (pair.Key.gameObject != null && pair.Key.gameObject.activeInHierarchy)
                    pageReports.Add(pair.Value.CaptureLayoutReport());
            var actionReports = new JArray();
            foreach (var view in hotkeys.Keys)
            {
                if (view.gameObject == null || !view.gameObject.activeInHierarchy) continue;
                var cells = ToolbarGroup(view).transform.GetComponentsInChildren<RectTransform>(false)
                    .Where(x => x.name.StartsWith("DialogueSave.Hotkey.", StringComparison.Ordinal) && x.gameObject.activeInHierarchy).ToList();
                var labels = new JArray(cells.Select(x => x.name.Substring("DialogueSave.Hotkey.".Length)));
                actionReports.Add(new JObject
                {
                    ["owner"] = view.GetType().Name,
                    ["nativePositionsUnchanged"] = hotkeys[view].NativeBaseline.All(pair => pair.Key != null &&
                        SameScreenBounds(pair.Value, UiLayoutDiagnostics.Rect((RectTransform)pair.Key.transform))),
                    ["labels"] = labels,
                    ["labelsUnique"] = labels.Select(x => x.ToString()).Distinct().Count() == labels.Count,
                    ["noOverlap"] = UiLayoutDiagnostics.DoNotOverlap(cells),
                    ["allCellRectangles"] = new JArray(ToolbarGroup(view).transform.Cast<Transform>()
                        .Where(t => t.gameObject.activeInHierarchy).Select(t => UiLayoutDiagnostics.Rect((RectTransform)t))),
                    ["allCellsDoNotOverlap"] = UiLayoutDiagnostics.DoNotOverlap(ToolbarGroup(view).transform.Cast<Transform>()
                        .Where(t => t.gameObject.activeInHierarchy).Select(t => (RectTransform)t)),
                    ["nativeCells"] = new JArray(ToolbarGroup(view).GetCells().OfType<Cell_KeyItemUI>().Select(c => new JObject
                    {
                        ["actionId"] = c.data == null ? JValue.CreateNull() : JToken.FromObject(c.data),
                        ["text"] = c.txtex_name.text, ["rect"] = UiLayoutDiagnostics.Rect(c.transform)
                    })),
                    ["textMeshes"] = new JArray(ToolbarGroup(view).transform.GetComponentsInChildren<TMP_SubMeshUI>(true).Select(m => new JObject
                    {
                        ["name"] = m.name, ["parent"] = m.transform.parent.name,
                        ["active"] = m.gameObject.activeInHierarchy, ["material"] = m.sharedMaterial == null ? null : m.sharedMaterial.name,
                        ["vertices"] = m.mesh == null ? 0 : m.mesh.vertexCount,
                        ["rect"] = UiLayoutDiagnostics.Rect((RectTransform)m.transform)
                    })),
                    ["rectangles"] = new JArray(cells.Select(UiLayoutDiagnostics.Rect)),
                    ["graphics"] = new JArray(cells.Select(c => new JObject { ["name"] = c.name, ["items"] = UiLayoutDiagnostics.Graphics(c) })),
                    ["bindings"] = new JArray(Enum.GetValues(typeof(DialogueHotkeyAction)).Cast<DialogueHotkeyAction>().Select(a =>
                    {
                        var binding = bindings?.GetBinding(a);
                        return new JObject { ["action"] = a.ToString(), ["key"] = binding == null ? null : binding.Label,
                            ["atlasUrl"] = binding == null ? null : binding.AtlasUrl,
                            ["renderLabel"] = binding != null && binding.RenderLabelOnKeycap };
                    }))
                });
            }
            return new JObject
            {
                ["screenWidth"] = Screen.width,
                ["screenHeight"] = Screen.height,
                ["note"] = "只读布局证据；不能替代截图、点击与恢复语义验收。空列表表示未观察到该界面。",
                ["saveWindows"] = pageReports,
                ["dialogueActionRows"] = actionReports,
                ["escapeMenus"] = new JArray(escapeMenus.Values.Select(m => m.CaptureLayoutReport())),
                ["actionDiagnostic"] = lastActionDiagnostic,
                ["visibleToolbarCount"] = new[] { UIMgr.GetView<TopView>(false), UIMgr.GetView<HotkeyView>(false) }
                    .Count(v => v != null && ToolbarGroup(v) != null && ToolbarGroup(v).gameObject.activeInHierarchy),
                ["globalRepeatedCaptions"] = new JArray(Resources.FindObjectsOfTypeAll<TMP_Text>()
                    .Where(t => t.gameObject.activeInHierarchy && (t.text == "日志" || t.text == "自动" || t.text == "菜单"))
                    .Select(t => new JObject { ["path"] = FullPath(t.transform), ["text"] = t.text,
                        ["rect"] = UiLayoutDiagnostics.Rect(t.rectTransform) })),
                ["activeNativeToolbarRoots"] = new JArray(Resources.FindObjectsOfTypeAll<RectTransform>()
                    .Where(r => r.gameObject.activeInHierarchy && r.name.StartsWith("HotkeyView", StringComparison.Ordinal))
                    .Select(r => new JObject { ["name"] = r.name, ["instanceId"] = r.gameObject.GetInstanceID(),
                        ["rect"] = UiLayoutDiagnostics.Rect(r), ["graphics"] = UiLayoutDiagnostics.Graphics(r) }))
            }.ToString(Formatting.Indented);
        }

        private static bool SameScreenBounds(JObject a, JObject b)
        {
            var first = a["screenBounds"]; var second = b["screenBounds"];
            return new[] { "x", "y", "width", "height" }.All(key =>
                Mathf.Abs(first.Value<float>(key) - second.Value<float>(key)) <= 1f);
        }

        private static string FullPath(Transform transform)
        {
            var names = new List<string>();
            while (transform != null) { names.Add(transform.name); transform = transform.parent; }
            names.Reverse(); return string.Join("/", names.ToArray());
        }

        private void Attach(SaveView view)
        {
            if (disposed || view == null || view.gameObject == null || originalArchives.Contains(view)) return;
            if(AdvDialogueController.Active?.IsAdv==true && service is DialogueSaveService archive)
            {
                if(!advPages.ContainsKey(view))
                {
                    if(!pendingMenu && !service.BeginMenu(false,out string reason)){service.Notify(reason);return;}
                    if(pages.TryGetValue(view,out var originalPage)){pages.Remove(view);originalPage.Dispose();if(!service.BeginMenu(false,out reason)){service.Notify(reason);return;}}
                    pendingMenu=false;advPages.Add(view,AdvSavePage.Open(view,archive));
                }
                return;
            }
            DialogueSavePage page;
            if (!pages.TryGetValue(view, out page))
            {
                page = new DialogueSavePage(view, service, log);
                pages.Add(view, page);
            }
            if (pendingMenu)
            {
                pendingMenu = false;
                page.Enter(true);
            }
            else page.PositionEntry();
        }

        private void Decorate(BaseView view)
        {
            if (decoratingHotkeys) return;
            decoratingHotkeys = true;
            try { DecorateCore(view); }
            finally { decoratingHotkeys = false; }
        }
        private void DecorateCore(BaseView view)
        {
            UpdateDuplicateRows();
            if (view != PrimaryToolbar()) return;
            if (!service.IsDialogueContext || !NativeControls || ToolbarGroup(view) == null || bindings == null)
                return;
            var top = UIMgr.GetTopView(ViewType.Guide, ViewType.Side);
            // Respect actual modal windows; CG/lyrics/animation states of the dialogue itself remain operable.
            if (!(top is NewTalkView)) return;
            HotkeyDecoration decoration;
            if (!hotkeys.TryGetValue(view, out decoration))
            {
                decoration = new HotkeyDecoration();
                hotkeys.Add(view, decoration);
            }
            var native = ToolbarGroup(view).GetCells().OfType<Cell_KeyItemUI>().ToList();
            bool nativeRowWasHidden = !ToolbarGroup(view).gameObject.activeSelf;
            if (nativeRowWasHidden)
            {
                decoration.ForcedGroup = ToolbarGroup(view).transform;
                decoration.OriginalGroupActive = false;
                // Preserve this owner's native anchor and position; TopView is not a HotkeyView clone.
                ToolbarGroup(view).gameObject.SetActive(true);
            }
            foreach (var cell in native.Where(x => nativeRowWasHidden ||
                (x.data is int && ((int)x.data == 105 || (int)x.data == 119))))
            {
                decoration.Hidden[cell.gameObject] = cell.gameObject.activeSelf;
                cell.gameObject.SetActive(false);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(ToolbarGroup(view).transform);
            Canvas.ForceUpdateCanvases();
            foreach (var cell in native.Where(c => c.gameObject.activeInHierarchy))
                decoration.NativeBaseline[cell.gameObject] = UiLayoutDiagnostics.Rect(cell.transform);
            foreach (DialogueHotkeyAction action in Enum.GetValues(typeof(DialogueHotkeyAction)))
                AddHotkey(view, decoration, action);
            LayoutRebuilder.MarkLayoutForRebuild(ToolbarGroup(view).transform);
        }

        private void AddHotkey(BaseView view, HotkeyDecoration decoration, DialogueHotkeyAction action)
        {
            // Pooled cells also include hidden Info/Menu entries. Match the visible
            // dialogue Log button, including its actual font material and text styling.
            var native = ToolbarGroup(view).GetCells().OfType<Cell_KeyItemUI>()
                .Where(c => c.gameObject.activeInHierarchy).ToList();
            var template = native.FirstOrDefault(c => c.data is int && (int)c.data == 103)
                ?? native.FirstOrDefault() ?? ToolbarTemplate(view);
            string label = DialogueKeycap.ActionLabel(action);
            var obj = NativeUiCloner.CloneCell(template.gameObject, ToolbarGroup(view).transform, "DialogueSave.Hotkey." + label);
            decoration.Added.Add(obj);
            var cell = new Cell_KeyItemUI(obj);
            cell.txtex_name.text = label;
            DialogueKeycap.Apply(cell, bindings.GetBinding(action));
            cell.txtex_name.ForceMeshUpdate();
            cell.transform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 65f + cell.txtex_name.preferredWidth);
            cell.btn_click.AddClick(() => Guard(() => ExecuteAction(action)));
            cell.btn_click.interactable = true;
            obj.SetActive(true);
            // The native layout is anchored to the right. Insert before its existing controls
            // so Log/Auto/Menu remain at their original screen positions.
            obj.transform.SetSiblingIndex((int)action);
        }

        private void ClearHotkeys(BaseView view)
        {
            HotkeyDecoration decoration;
            if (hotkeys.TryGetValue(view, out decoration)) decoration.Clear();
        }

        private void Closed(BaseView view)
        {
            var save = view as SaveView;
            if(save!=null && originalArchives.Remove(save))service.EndMenu();
            if (!disposed && AdvDialogueController.Active?.IsAdv==true && AdvSettingsTransition.Active==null &&
                ((save!=null && (pages.ContainsKey(save) || advPages.ContainsKey(save))) || (view is EntryView && escapeMenus.ContainsKey((EntryView)view))))
                AdvSettingsTransition.Play(AdvSettingsTransition.Capture(),false,sortingOrder:32500);
            if(save!=null && advPages.TryGetValue(save,out var archivePage)){advPages.Remove(save);archivePage.Close();}
            if (save != null && pendingMenu) CancelPending();
            DialogueSavePage page;
            if (save != null && pages.TryGetValue(save, out page))
            {
                pages.Remove(save);
                page.Dispose();
            }
            var menu = view as EntryView;
            DialogueEscapeMenu escape;
            if (menu != null && escapeMenus.TryGetValue(menu, out escape))
            { escapeMenus.Remove(menu); escape.Dispose(); }
            if (view is HotkeyView || view is TopView)
            {
                ClearHotkeys(view); hotkeys.Remove(view);
                var group = ToolbarGroup(view);
                if (group != null) suppressedRows.Remove(group.gameObject);
            }
        }

        private void CancelPending()
        {
            if (!pendingMenu) return;
            pendingMenu = false;
            service.EndMenu();
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { log("对话 UI 操作已中止，原版 UI 保持可用：" + ex); }
        }

        private void Patch(Type type, string originalName, string patchName, bool prefix, Type[] arguments = null)
        {
            var original = AccessTools.Method(type, originalName, arguments);
            var patch = AccessTools.Method(typeof(DialogueUiController), patchName);
            if (original == null || patch == null) throw new MissingMethodException(type.FullName, originalName);
            harmony.Patch(original, prefix ? new HarmonyMethod(patch) : null,
                prefix ? null : new HarmonyMethod(patch));
            patches.Add(Tuple.Create((MethodBase)original, patch));
        }

        private static void ArchiveConstructed(SaveView __instance)=>ConfigureArchiveLoading(__instance);
        private static void ArchiveRetrieved(BaseView __result){if(__result is SaveView view)ConfigureArchiveLoading(view);}
        private static void ConfigureArchiveLoading(SaveView view)
        {
            if(active==null)return;
            if(!active.loadingDefaults.TryGetValue(view,out bool original))active.loadingDefaults[view]=original=view.isShowLoading;
            view.isShowLoading=AdvDialogueController.Active?.IsAdv==true && !active.originalArchives.Contains(view)?false:original;
        }

        private static bool NativeTabSelected(UIToggleGroup __instance)
        {
            var controller = active;
            if (controller == null) return true;
            foreach (var page in controller.pages.Values.ToArray())
            {
                if (!page.OwnsNativeTabGroup(__instance)) continue;
                // Fail closed if dialogue-menu cleanup throws: never accidentally invoke native SaveGame.
                bool allow = false;
                controller.Guard(() => allow = page.OnNativeTabSelected(__instance));
                return allow;
            }
            return true;
        }

        internal static bool IsTitleScreen()
        {
            var entry=UIMgr.GetView<EntryView>(false) as EntryView;
            return Game.GetGameState()!=GameState.Running || (entry?.viewState==ViewState.Opened && !(bool)AccessTools.Field(typeof(EntryView),"isPause").GetValue(entry));
        }
        private static bool EntrySavePrefix()
        {
            if(active==null || AdvDialogueController.Active?.IsAdv!=true)return true;
            if(IsTitleScreen())return false;
            active.Guard(()=>active.Open(true));return false;
        }
        internal static void OpenOriginalArchive(SaveView view)
        {
            if(active==null || view==null || !active.advPages.TryGetValue(view,out var page))return;
            active.originalArchives.Add(view);active.advPages.Remove(view);page.Close();
            // Scope the bypass to this open view. Closing it restores ADV routing.
            active.service.BeginMenu(false,out _);
            view.parms[0]=false;view.OnOpen();view.Refresh();
        }

        private static void SaveOpened(SaveView __instance)
        {
            if (active != null) active.Guard(() => active.Attach(__instance));
        }
        private static bool SaveRefreshPrefix(SaveView __instance)
        {
            if(active==null || AdvDialogueController.Active?.IsAdv!=true || active.originalArchives.Contains(__instance))return true;
            if(__instance.isViewReady)active.Guard(()=>active.Attach(__instance));
            return !active.advPages.ContainsKey(__instance);
        }

        private static void SaveRefreshed(SaveView __instance)
        {
            if (active == null) return;
            DialogueSavePage page;
            if (active.pages.TryGetValue(__instance, out page)) active.Guard(page.PositionEntry);
        }
        private static void DialogueOpened(NewTalkView __instance)
        {
            if (active != null) active.Guard(active.RefreshActions);
        }
        private static void EntryRefreshed(EntryView __instance)
        {
            if (active != null) active.Guard(() => active.DecorateEntry(__instance));
        }
        private static void HotkeysBeforeRefresh(BaseView __instance)
        {
            if (active != null) active.Guard(() =>
            {
                active.ClearHotkeys(__instance);
                var group = ToolbarGroup(__instance);
                bool prior;
                if (group != null && active.suppressedRows.TryGetValue(group.gameObject, out prior))
                {
                    active.suppressedRows.Remove(group.gameObject);
                    group.gameObject.SetActive(prior);
                }
            });
        }
        private static void HotkeysRefreshed(BaseView __instance)
        {
            if (active != null) active.Guard(() => active.Decorate(__instance));
        }
        private static bool EntryHotkey(EntryView __instance, int _keyAction, ref bool __result)
        {
            if (active == null || !active.service.IsDialogueContext || !DialogueEscapeMenu.IsDialogueMenu(__instance) ||
                (_keyAction != 105 && _keyAction != 119)) return true;
            __result = true;
            return false;
        }
        private static bool DialogueHotkey(int _keyAction, ref bool __result)
        {
            var controller = active;
            if (controller == null || !controller.service.IsDialogueContext ||
                (_keyAction != 105 && _keyAction != 119)) return true;
            __result = true;
            // Dedicated bindings own dispatch; consume legacy 105/119 without a second alias.
            return false;
        }
        private static void ViewClosed(BaseView __instance)
        {
            if (active != null) active.Guard(() => active.Closed(__instance));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (active == this) active = null;
            Guard(CancelPending);
            if (bindings != null) { bindings.BindingsChanged -= RefreshActions; Guard(bindings.Dispose); bindings = null; }
            foreach (var menu in escapeMenus.Values.ToArray()) Guard(menu.Dispose);
            escapeMenus.Clear();
            foreach (var page in pages.Values.ToArray()) Guard(page.Dispose);
            pages.Clear();
            foreach(var pair in loadingDefaults)pair.Key.isShowLoading=pair.Value;loadingDefaults.Clear();
            foreach(var page in advPages.Values.ToArray())Guard(page.Close);advPages.Clear();
            foreach (var decoration in hotkeys.Values) Guard(decoration.Clear);
            hotkeys.Clear();
            foreach (var pair in suppressedRows) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            suppressedRows.Clear();
            if (harmony != null)
                foreach (var patch in patches) Guard(() => harmony.Unpatch(patch.Item1, patch.Item2));
            patches.Clear();
        }
    }
}
