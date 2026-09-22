using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Config;
using Components;
using GenUI.Main;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Main;
using View.Common;
using Object = UnityEngine.Object;

namespace StudentAgeDialogueSave.UI
{
    // Owns only cloned presentation objects. The original SaveView and its data stay intact.
    internal sealed class DialogueSavePage : IDisposable
    {
        private readonly SaveView view;
        private readonly IDialogueUiService service;
        private readonly Action<string> log;
        private readonly List<GameObject> pageObjects = new List<GameObject>();
        private readonly Dictionary<GameObject, bool> hidden = new Dictionary<GameObject, bool>();
        private readonly List<Cell_SaveItemUI> cards = new List<Cell_SaveItemUI>();
        private readonly List<Cell_DetailTopItemUI> tabs = new List<Cell_DetailTopItemUI>();
        private readonly Cell_DetailTopItemUI entry;
        private GameObject grid;
        private Cell_SaveItemUI cardStyleSource;
        private RectTransform secondaryTabs;
        private Text currentText, currentPageText, totalPageText;
        private UIButton previous, next, commit;
        private DialogueUiCategory category;
        private List<SlotItem> items = new List<SlotItem>();
        private SlotItem selected;
        private int page = 1;
        private int generation;
        private bool busy;
        private bool entered;
        private bool disposed;
        private bool ownsMenu;
        private bool returnToDialogue;
        private string pendingSaveKey;
        private string operationError;

        private sealed class SlotItem
        {
            internal int Slot;
            internal DialogueUiRecord Record;
        }

        internal DialogueSavePage(SaveView view, IDialogueUiService service, Action<string> log)
        {
            this.view = view;
            this.service = service;
            this.log = log;
            entry = CloneTab(view.tabgroup_top.transform, "DialogueSave.Entry", "对话存档", () => Enter(false));
            IgnoreLayout(entry.transform);
        }

        internal bool IsEntered { get { return entered; } }
        internal bool OwnsMenuLease { get { return ownsMenu; } }

        internal void PositionEntry()
        {
            if (disposed || entry.gameObject == null) return;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(view.tabgroup_top.transform);
            var first = view.tabgroup_top.GetCells().FirstOrDefault();
            if (first != null) PositionBefore(entry.transform, first.transform, view.tabgroup_top.transform);
            if (entered && tabs.Count > 0) PositionSecondaryTabs();
        }

        internal bool OwnsNativeTabGroup(UIToggleGroup group) { return entered && group == view.tabgroup_top; }

        internal bool OnNativeTabSelected(UIToggleGroup group)
        {
            if (!entered || group != view.tabgroup_top) return true;
            // Only saving is confined to dialogue slots. Native load categories share
            // this window's pause lease until cancellation or native world loading closes it.
            if (view.isSaveMode && returnToDialogue) { UIMgr.CloseView(view); return false; }
            Leave(keepMenu: !view.isSaveMode);
            return true;
        }

        private void SetEntrySelected(bool active)
        {
            entry.img_select.gameObject.SetActive(active);
            entry.icon_item.gameObject.SetActive(!active);
            foreach (var tab in view.tabgroup_top.GetCells().OfType<Cell_DetailTopItemUI>())
            {
                bool selectedNative = !active && view.tabgroup_top.curSelectCell == tab;
                tab.img_select.gameObject.SetActive(selectedNative);
                tab.icon_item.gameObject.SetActive(!selectedNative);
            }
        }

        private void PositionSecondaryTabs()
        {
            var first = view.tabgroup_top.GetCells().FirstOrDefault();
            if (first == null || secondaryTabs == null || grid == null) return;
            var parent = (RectTransform)view.tabgroup_top.transform.parent;
            secondaryTabs.anchoredPosition = view.tabgroup_top.transform.anchoredPosition;
            var gridRect = (RectTransform)grid.transform;
            gridRect.anchoredPosition = view.itemgroup_save.transform.anchoredPosition;
            LayoutRebuilder.ForceRebuildLayoutImmediate(secondaryTabs);
            LayoutRebuilder.ForceRebuildLayoutImmediate(gridRect);
            var nativeLayout = view.tabgroup_top.transform.GetComponent<HorizontalLayoutGroup>();
            float gap = nativeLayout == null ? 0 : nativeLayout.spacing;
            var nativeBounds = BoundsIn(parent, first.transform);
            var secondaryBounds = BoundsIn(parent, tabs[0].transform);
            // Keep the original controls at their exact size and derive offsets from live geometry.
            float rowShift = nativeBounds.yMin - gap - secondaryBounds.yMax;
            secondaryTabs.localPosition += new Vector3(0, rowShift, 0);
            secondaryBounds = BoundsIn(parent, tabs[0].transform);
            var gridBounds = BoundsIn(parent, gridRect);
            var layout = gridRect.GetComponent<GridLayoutGroup>();
            float paddingTop = layout == null ? 0 : layout.padding.top;
            float firstCardTop = gridBounds.yMax - paddingTop;
            float shift = Mathf.Min(0, secondaryBounds.yMin - gap - firstCardTop);
            gridRect.localPosition += new Vector3(0, shift, 0);
        }

        private static Rect BoundsIn(RectTransform parent, RectTransform child)
        {
            var corners = new Vector3[4];
            child.GetWorldCorners(corners);
            var points = corners.Select(parent.InverseTransformPoint).ToArray();
            return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
        }

        internal void Enter(bool menuAlreadyBegun)
        {
            if (disposed || entered) return;
            string reason;
            if (!menuAlreadyBegun && !ownsMenu && !service.BeginMenu(view.isSaveMode, out reason))
            {
                service.Notify(reason);
                return;
            }
            ownsMenu = true;
            // A toolbar save is not an alternative way around the game's enableSave gate.
            returnToDialogue = menuAlreadyBegun && view.isSaveMode;
            try
            {
                BuildPage();
                entered = true;
                category = DialogueUiCategory.Manual;
                page = 1;
                selected = null;
                Refresh();
                PositionEntry();
            }
            catch (Exception ex)
            {
                log("对话存档页建立失败：" + ex);
                Leave();
                service.Notify("对话存档界面暂不可用，原版存档界面未更改。");
            }
        }

        private void BuildPage()
        {
            Hide(view.itemgroup_save.gameObject);
            SetEntrySelected(true);
            Hide(view.txt_cur.gameObject);
            Hide(view.txt_page_cur.gameObject);
            Hide(view.txt_page_total.gameObject);
            Hide(view.btn_prev.gameObject);
            Hide(view.btn_next.gameObject);
            Hide(view.btn_save.gameObject);
            Hide(view.btn_load.gameObject);
            Hide(view.txt_landTips.gameObject);

            grid = Own(NativeUiCloner.EmptyContainer(view.itemgroup_save.transform, "DialogueSave.Grid"));
            grid.SetActive(true);
            for (int i = 0; i < 9; i++)
            {
                cardStyleSource = view.itemgroup_save.GetCells().OfType<Cell_SaveItemUI>().FirstOrDefault() ?? view.Cell_SaveItem;
                var obj = NativeUiCloner.CloneCell(cardStyleSource.gameObject, grid.transform, "DialogueSave.Card." + i);
                var card = new Cell_SaveItemUI(obj);
                // Do not retain the native note editor or original-file deletion callbacks.
                card.btn_note.interactable = false;
                card.btn_note.btn.transition = Selectable.Transition.None;
                card.btn_note.btn.enabled = false;
                card.txt_note.supportRichText = false;
                card.txt_item.supportRichText = false;
                card.txtex_year.richText = false;
                card.txtex_season.richText = false;
                int index = i;
                card.btn_click.AddClick(() => Select(index));
                card.btn_add.AddClick(() => Create(index));
                card.btn_delete.AddClick(() => Delete(index));
                cards.Add(card);
            }

            // Saving has only manual dialogue slots; reading exposes separate secondary categories.
            if (!view.isSaveMode)
            {
                var top = Own(NativeUiCloner.EmptyContainer(view.tabgroup_top.transform, "DialogueSave.Categories"));
                top.SetActive(true);
                secondaryTabs = (RectTransform)top.transform;
                foreach (DialogueUiCategory cat in Enum.GetValues(typeof(DialogueUiCategory)))
                {
                    DialogueUiCategory captured = cat;
                    tabs.Add(CloneTab(secondaryTabs, "DialogueSave.Category." + cat, CategoryName(cat), () => Switch(captured)));
                }
            }

            // Native txt_cur is only a short selected-slot caption. The native wide bottom hint
            // provides the existing readable area for dialogue identity, state, and line summary.
            currentText = CloneText(view.txt_landTips, "DialogueSave.Description");
            // The native bottom hint deliberately overflows its narrow centered RectTransform.
            // Its available visual line is wide; card-style ellipsis would cut it to a few glyphs.
            currentText.horizontalOverflow = HorizontalWrapMode.Overflow;
            currentPageText = CloneText(view.txt_page_cur, "DialogueSave.Page");
            totalPageText = CloneText(view.txt_page_total, "DialogueSave.TotalPages");
            previous = NativeUiCloner.CloneButton(view.btn_prev, "DialogueSave.Previous", () => Move(-1));
            next = NativeUiCloner.CloneButton(view.btn_next, "DialogueSave.Next", () => Move(1));
            commit = NativeUiCloner.CloneButton(view.isSaveMode ? view.btn_save : view.btn_load,
                "DialogueSave.Commit", Commit);
            Own(previous.gameObject); Own(next.gameObject); Own(commit.gameObject);
        }

        private void Switch(DialogueUiCategory value)
        {
            if (busy) return;
            category = value;
            page = 1;
            selected = null;
            Refresh();
        }

        internal void RefreshRecords() { Refresh(); }

        private void Refresh()
        {
            if (!entered || disposed) return;
            IReadOnlyList<DialogueUiRecord> records = service.List(category) ?? new DialogueUiRecord[0];
            items = new List<SlotItem>();
            if (category == DialogueUiCategory.Manual && view.isSaveMode)
            {
                var recordsBySlot = records.ToLookup(x => x.Slot);
                for (int slot = 1; slot <= 99; slot++)
                {
                    var inSlot = recordsBySlot[slot].OrderByDescending(x => x.CreatedUtc).ToList();
                    if (inSlot.Count == 0) items.Add(new SlotItem { Slot = slot });
                    else foreach (var record in inSlot) items.Add(new SlotItem { Slot = slot, Record = record });
                }
                // Preserve visibility of imported/out-of-range slots rather than silently omitting records.
                foreach (var record in records.Where(x => x.Slot < 1 || x.Slot > 99))
                    items.Add(new SlotItem { Slot = record.Slot, Record = record });
            }
            else
            {
                foreach (var record in records.OrderByDescending(x => x.CreatedUtc))
                    items.Add(new SlotItem { Slot = record.Slot, Record = record });
            }
            // A background scan may fill a slot that was empty when it was selected.
            // Never turn that stale selection into an unconfirmed overwrite.
            if (selected != null)
            {
                selected = selected.Record == null ?
                    items.FirstOrDefault(x => x.Slot == selected.Slot && x.Record == null) :
                    items.FirstOrDefault(x => x.Record != null && x.Record.RevisionId == selected.Record.RevisionId);
            }
            int count = Math.Max(1, (items.Count + 8) / 9);
            page = Math.Max(1, Math.Min(page, count));
            currentPageText.text = page.ToString(CultureInfo.InvariantCulture);
            totalPageText.text = count.ToString(CultureInfo.InvariantCulture);
            previous.interactable = !busy && page > 1;
            next.interactable = !busy && page < count;
            for (int i = 0; i < tabs.Count; i++)
            {
                bool active = i == (int)category;
                tabs[i].img_select.gameObject.SetActive(active);
                tabs[i].icon_item.gameObject.SetActive(!active);
                tabs[i].btn_click.interactable = !busy;
            }
            for (int i = 0; i < cards.Count; i++) RenderCard(cards[i], ItemAt(i));
            RefreshSelection();
        }

        private SlotItem ItemAt(int index)
        {
            int absolute = (page - 1) * 9 + index;
            return absolute >= 0 && absolute < items.Count ? items[absolute] : null;
        }

        private void RenderCard(Cell_SaveItemUI card, SlotItem item)
        {
            card.gameObject.SetActive(item != null);
            if (item == null) return;
            var record = item.Record;
            card.icon_item.SetAtlasUrl(category == DialogueUiCategory.Manual ? "save/btn_manualsave" :
                category == DialogueUiCategory.Auto ? "save/btn_autosave" : "save/btn_quicksave");
            card.txt_idx.text = item.Slot > 0 ? item.Slot.ToString(CultureInfo.InvariantCulture) : "-";
            card.btn_click.gameObject.SetActive(record != null);
            card.btn_add.gameObject.SetActive(record == null);
            card.txt_add.gameObject.SetActive(view.isSaveMode && category == DialogueUiCategory.Manual);
            bool isSelected = selected != null && selected.Slot == item.Slot &&
                (selected.Record == null ? record == null : record != null && selected.Record.RevisionId == record.RevisionId);
            // Native DisabledColor shows the yellow selection frame. Disabling every
            // slot during an async save would therefore highlight the entire grid.
            // Handlers guard busy; only the actual selected slot uses native disabled styling.
            card.btn_add.interactable = view.isSaveMode && category == DialogueUiCategory.Manual && !isSelected;
            card.btn_click.interactable = !isSelected;
            if (record == null)
            {
                var stale = card.btn_note.gameObject.GetComponent<Description>();
                if (stale != null) { stale.OnPointerExit(null); stale.getDescription = null; }
                return;
            }
            card.txt_item.text = Ellipsize(card.txt_item, string.IsNullOrWhiteSpace(record.Speaker) ? "旁白" : record.Speaker);
            card.txt_note.text = Ellipsize(card.txt_note, record.Summary);
            var description = card.btn_note.gameObject.GetComponent<Description>() ?? card.btn_note.gameObject.AddComponent<Description>();
            description.getDescription = index => index == 0 ? new DescData
            {
                title = SafeDescription(string.IsNullOrWhiteSpace(record.Speaker) ? "旁白" : record.Speaker),
                txt = SafeDescription(record.Summary)
            } : (DescData?)null;
            card.txt_scene.text = Ellipsize(card.txt_scene, record.Location);
            card.txtex_year.text = record.YearLabel ?? "";
            card.txtex_season.text = record.SeasonLabel ?? "";
            var gradient = view.Cell_SaveItem.txtex_year.colorGradient;
            SeasonCfg season = ResolveSeason(record);
            if (season != null && season.colors != null && season.colors.Count >= 2)
            {
                gradient.topLeft = gradient.topRight = ColorCtrl.Parse(season.colors[0]);
                gradient.bottomLeft = gradient.bottomRight = ColorCtrl.Parse(season.colors[1]);
            }
            card.txtex_year.colorGradient = gradient;
            card.txtex_season.colorGradient = gradient;
            card.txt_time.text = record.CreatedUtc == default(DateTime) ? "时间未知" :
                record.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            card.txt_ago.text = record.IsConflict ? "存在分叉" : !record.CanLoad ? "不可读取" : "";
            card.root_auto.gameObject.SetActive(category == DialogueUiCategory.Auto);
            card.root_quick.gameObject.SetActive(category == DialogueUiCategory.Quick);
            card.btn_delete.interactable = !busy;
            if ((record.Gender == 1 || record.Gender == 2) && Cfg.PersonCfgMap.ContainsKey(0))
                card.icon_head.SetTextureUrl(Cfg.PersonCfgMap[0].GetHeadIcon(
                    record.Gender == 1 ? GenderDefine.Male : GenderDefine.Female, 0, record.GradeState));
            else card.icon_head.Clear();
        }

        private void Select(int index)
        {
            if (busy) return;
            operationError = null;
            selected = ItemAt(index);
            Refresh();
        }

        private void Create(int index)
        {
            if (disposed || !entered || busy || !view.isSaveMode || category != DialogueUiCategory.Manual) return;
            var item = ItemAt(index);
            if (item == null || item.Record != null) return;
            Select(index);
            // Refresh invalidates an empty selection if a background scan filled it;
            // Commit must never silently reinterpret that click as an overwrite.
            Commit();
        }

        private void RefreshSelection()
        {
            string reason = "";
            if (view.isSaveMode && category != DialogueUiCategory.Manual)
                reason = "自动与快速存档由系统管理，请切换手动存档保存。";
            var record = selected == null ? null : selected.Record;
            bool canReplace = record == null || (record.CanLoad && !record.IsConflict);
            commit.interactable = selected != null && (view.isSaveMode ?
                category == DialogueUiCategory.Manual && canReplace : record != null && record.CanLoad);
            string prefix = "对话存档 · " + CategoryName(category);
            if (!string.IsNullOrEmpty(operationError)) currentText.text = operationError;
            else if (!string.IsNullOrEmpty(service.StatusMessage)) currentText.text = service.StatusMessage;
            else if (view.isSaveMode && service.IsPreparingSave)
                currentText.text = busy ? "已受理，正在准备存档…" : "正在准备存档…";
            else if (service.IsListing) currentText.text = "正在读取存档…";
            else if (busy) currentText.text = "正在处理，请稍候…";
            else if (view.isSaveMode && !string.IsNullOrEmpty(reason)) currentText.text = prefix + " · " + reason;
            else if (record != null && !record.CanLoad) currentText.text = prefix + " · " + record.StatusReason;
            else if (record != null && record.IsConflict) currentText.text = prefix + " · 分叉版本，请核对时间和台词后选择读取；不会自动覆盖。";
            else if (record != null) currentText.text = prefix + " · " + record.Speaker + "：" + record.Summary;
            else currentText.text = prefix + (items.Count == 0 ? " · 暂无存档" : " · 请选择存档位置");
            currentText.text = Ellipsize(currentText, currentText.text, view.itemgroup_save.transform.rect.width);
        }

        private void Move(int delta)
        {
            if (busy) return;
            page += delta;
            selected = null;
            Refresh();
        }

        private void Commit()
        {
            if (selected == null || !commit.interactable) return;
            var target = selected;
            if (view.isSaveMode)
            {
                string key = target.Slot + ":" + (target.Record == null ? "" : target.Record.RevisionId);
                Action write = () =>
                {
                    pendingSaveKey = key;
                    Run(done => service.Save(DialogueUiCategory.Manual, target.Slot,
                        target.Record == null ? null : target.Record.RevisionId, done), false);
                };
                if (target.Record == null || (busy && pendingSaveKey == key)) write();
                else HintHelper.ShowConfirm("覆盖这个对话存档？原版手动存档不受影响。", () =>
                {
                    if (!disposed && entered) write();
                });
            }
            else if (target.Record != null)
            {
                HintHelper.ShowConfirm("读取这个对话存档？将离开当前进度。", () =>
                {
                    if (!disposed && entered)
                        Run(done => service.Load(target.Record.RevisionId, done), true);
                });
            }
        }

        private void Delete(int index)
        {
            if (busy) return;
            var item = ItemAt(index);
            if (item == null || item.Record == null) return;
            string revision = item.Record.RevisionId;
            HintHelper.ShowConfirm("删除这个对话存档版本？其他存档不受影响。", () =>
            {
                if (!disposed && entered && !busy)
                    Run(done => service.Delete(revision, done), false);
            });
        }

        private void Run(Action<Action<UiResult>> start, bool closeOnSuccess)
        {
            // Repeated clicks share the current UI operation generation; the service owns deduplication.
            int token = busy ? generation : ++generation;
            operationError = null;
            busy = true;
            Refresh();
            try
            {
                start(result =>
                {
                    if (disposed || !entered || generation != token) return;
                    if (result.IsPending)
                    {
                        busy = true;
                        // The native bottom hint already shows the pending state. A toast would
                        // linger over the cards even after the completed-save toast appears.
                        RefreshSelection();
                        return;
                    }
                    busy = false;
                    pendingSaveKey = null;
                    operationError = result.Success ? null : result.Message;
                    if (result.Success && closeOnSuccess) UIMgr.CloseView(view);
                    else { selected = null; Refresh(); }
                });
            }
            catch (Exception ex)
            {
                busy = false;
                pendingSaveKey = null;
                log("对话存档操作失败：" + ex);
                operationError = "操作未完成，当前存档不会被界面自动覆盖。";
                Refresh();
            }
        }

        internal void Leave(bool keepMenu = false)
        {
            ++generation;
            entered = false;
            returnToDialogue = false;
            busy = false;
            foreach (var obj in pageObjects) NativeUiCloner.Remove(obj);
            pageObjects.Clear(); cards.Clear(); tabs.Clear();
            foreach (var pair in hidden) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            hidden.Clear();
            selected = null;
            secondaryTabs = null;
            pendingSaveKey = null;
            operationError = null;
            SetEntrySelected(false);
            if (ownsMenu && !keepMenu)
            {
                ownsMenu = false;
                try { service.EndMenu(); }
                catch (Exception ex) { log("释放对话窗口暂停失败：" + ex); }
            }
            if (!disposed) PositionEntry();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Leave(); }
            finally { NativeUiCloner.Remove(entry.gameObject); }
        }

        private GameObject Own(GameObject obj) { pageObjects.Add(obj); return obj; }
        private void Hide(GameObject obj) { if (!hidden.ContainsKey(obj)) hidden[obj] = obj.activeSelf; obj.SetActive(false); }
        private Text CloneText(Text source, string name)
        {
            var obj = Own(NativeUiCloner.Clone(source.gameObject, source.transform.parent, name));
            obj.SetActive(true);
            var text = obj.GetComponent<Text>();
            text.supportRichText = false;
            return text;
        }

        private Cell_DetailTopItemUI CloneTab(Transform parent, string name, string label, Action click)
        {
            // Prefer a fully laid-out native tab: the pooled template is not a visible-size baseline.
            var source = view.tabgroup_top.GetCells().OfType<Cell_DetailTopItemUI>().FirstOrDefault();
            var obj = NativeUiCloner.CloneCell(source == null ? view.root_Cell_DetailTopItem.gameObject : source.gameObject, parent, name);
            var tab = new Cell_DetailTopItemUI(obj);
            tab.txt_item.text = label;
            tab.txt_select.text = label;
            tab.img_select.gameObject.SetActive(false);
            tab.icon_item.gameObject.SetActive(true);
            tab.btn_click.AddClick(() => click());
            obj.SetActive(true);
            return tab;
        }

        private static void IgnoreLayout(RectTransform rect)
        {
            var layout = rect.GetComponent<LayoutElement>() ?? rect.gameObject.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
        }

        private static void PositionBefore(RectTransform target, RectTransform source, RectTransform parent)
        {
            var layout = parent.GetComponent<HorizontalLayoutGroup>();
            float gap = layout == null ? 0f : layout.spacing;
            target.anchorMin = source.anchorMin; target.anchorMax = source.anchorMax;
            target.pivot = source.pivot; target.sizeDelta = source.sizeDelta;
            target.localScale = source.localScale; target.localRotation = source.localRotation;
            target.anchoredPosition = source.anchoredPosition - new Vector2(source.rect.width + gap, 0);
        }

        private static string CategoryName(DialogueUiCategory cat)
        {
            return cat == DialogueUiCategory.Manual ? "手动存档" : cat == DialogueUiCategory.Auto ? "自动存档" : "快速存档";
        }

        private static string SafeDescription(string value)
        {
            return (value ?? "").Replace("<", "＜").Replace(">", "＞");
        }

        private static SeasonCfg ResolveSeason(DialogueUiRecord record)
        {
            SeasonCfg season;
            if (record.SeasonId.HasValue && Cfg.SeasonCfgMap.TryGetValue(record.SeasonId.Value, out season)) return season;
            if (record.SeasonId.HasValue || string.IsNullOrEmpty(record.SeasonLabel)) return null;
            var matches = Cfg.SeasonCfgMap.Values.Where(x => x.name == record.SeasonLabel).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        internal JObject CaptureLayoutReport()
        {
            var nativeTabs = new JArray();
            foreach (var native in view.tabgroup_top.GetCells().OfType<Cell_DetailTopItemUI>())
                nativeTabs.Add(new JObject
                {
                    ["id"] = native.data == null ? JValue.CreateNull() : JToken.FromObject(native.data),
                    ["normalLabel"] = native.txt_item.text,
                    ["selectedLabel"] = native.txt_select.text,
                    ["rect"] = UiLayoutDiagnostics.Rect(native.transform)
                });
            var report = new JObject
            {
                ["saving"] = view.isSaveMode,
                ["layer"] = view.layerType.ToString(),
                ["dialoguePageOpen"] = entered,
                ["ownsMenuLease"] = ownsMenu,
                ["nativeTabsVisible"] = view.tabgroup_top.gameObject.activeInHierarchy &&
                    view.tabgroup_top.GetCells().All(x => x.gameObject.activeInHierarchy),
                ["category"] = category.ToString(),
                ["nativeTabs"] = nativeTabs,
                ["nativeTabCountUnchanged"] = nativeTabs.Count == (view.isSaveMode ? 1 : 3),
                ["entryLabel"] = entry.txt_item.text,
                ["entrySelectedLabel"] = entry.txt_select.text,
                ["entry"] = UiLayoutDiagnostics.Rect(entry.transform),
                ["entryGraphics"] = UiLayoutDiagnostics.Graphics(entry.transform),
                ["nativeCardTemplate"] = UiLayoutDiagnostics.Rect(view.Cell_SaveItem.transform)
            };
            var firstNativeTab = view.tabgroup_top.GetCells().FirstOrDefault();
            if (firstNativeTab != null) report["firstNativeTabGraphics"] = UiLayoutDiagnostics.Graphics(firstNativeTab.transform);
            var nativeGridLayout = view.itemgroup_save.transform.GetComponent<GridLayoutGroup>();
            if (nativeGridLayout != null) report["nativeGridCellSize"] = new JArray(nativeGridLayout.cellSize.x, nativeGridLayout.cellSize.y);
            var cardReports = new JArray();
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                var item = ItemAt(i);
                var record = item == null ? null : item.Record;
                var season = record == null ? null : ResolveSeason(record);
                var year = card.txtex_year.colorGradient;
                var seasonGradient = card.txtex_season.colorGradient;
                bool? expectedGradient = season != null && season.colors != null && season.colors.Count >= 2 ?
                    (bool?)(UiLayoutDiagnostics.SameColor(year.topLeft, ColorCtrl.Parse(season.colors[0])) &&
                    UiLayoutDiagnostics.SameColor(year.topRight, ColorCtrl.Parse(season.colors[0])) &&
                    UiLayoutDiagnostics.SameColor(year.bottomLeft, ColorCtrl.Parse(season.colors[1])) &&
                    UiLayoutDiagnostics.SameColor(year.bottomRight, ColorCtrl.Parse(season.colors[1])) &&
                    UiLayoutDiagnostics.SameGradient(year, seasonGradient)) : null;
                cardReports.Add(new JObject
                {
                    ["index"] = i,
                    ["rect"] = UiLayoutDiagnostics.Rect(card.transform),
                    ["sameNativeCardSize"] = SameNativeCardSize(card.transform),
                    ["speaker"] = UiLayoutDiagnostics.Text(card.txt_item),
                    ["summary"] = UiLayoutDiagnostics.Text(card.txt_note),
                    ["sameSpeakerFont"] = UiLayoutDiagnostics.SameFont(card.txt_item, (cardStyleSource ?? view.Cell_SaveItem).txt_item),
                    ["sameSummaryFont"] = UiLayoutDiagnostics.SameFont(card.txt_note, (cardStyleSource ?? view.Cell_SaveItem).txt_note),
                    ["seasonId"] = record == null || !record.SeasonId.HasValue ? JValue.CreateNull() : new JValue(record.SeasonId.Value),
                    ["yearGradient"] = UiLayoutDiagnostics.Gradient(year),
                    ["seasonGradient"] = UiLayoutDiagnostics.Gradient(seasonGradient),
                    ["matchesSeasonConfig"] = expectedGradient.HasValue ? new JValue(expectedGradient.Value) : JValue.CreateNull()
                });
            }
            report["cards"] = cardReports;
            report["visibleCardsAtMostNine"] = cards.Count(x => x.gameObject.activeInHierarchy) <= 9;
            if (entered)
            {
                report["originalGridHidden"] = !view.itemgroup_save.gameObject.activeSelf;
                report["originalTabsVisible"] = view.tabgroup_top.gameObject.activeInHierarchy;
                report["grid"] = UiLayoutDiagnostics.Rect((RectTransform)grid.transform);
                report["secondaryTabs"] = new JArray(tabs.Select(t => new JObject
                {
                    ["label"] = t.txt_item.text, ["rect"] = UiLayoutDiagnostics.Rect(t.transform),
                    ["graphics"] = UiLayoutDiagnostics.Graphics(t.transform)
                }));
                report["hierarchyDoesNotOverlap"] = UiLayoutDiagnostics.DoNotOverlap(
                    tabs.Select(t => t.transform).Concat(view.tabgroup_top.GetCells().Select(t => t.transform))
                        .Concat(new[] { entry.transform }).Concat(cards.Where(c => c.gameObject.activeInHierarchy).Select(c => c.transform)));
                report["cardsDoNotOverlapFooter"] = UiLayoutDiagnostics.DoNotOverlap(
                    cards.Where(c => c.gameObject.activeInHierarchy).Select(c => c.transform)
                        .Concat(new[] { commit.transform, previous.transform, next.transform, currentText.rectTransform }));
                report["description"] = UiLayoutDiagnostics.Text(currentText);
                report["cardsDoNotOverlap"] = UiLayoutDiagnostics.DoNotOverlap(cards.Where(x => x.gameObject.activeInHierarchy).Select(x => x.transform));
                report["cardsInsideGrid"] = cards.Where(x => x.gameObject.activeInHierarchy).All(x =>
                    UiLayoutDiagnostics.Contains((RectTransform)grid.transform, x.transform));
            }
            return report;
        }

        private bool SameNativeCardSize(RectTransform rect)
        {
            var nativeGrid = view.itemgroup_save.transform.GetComponent<GridLayoutGroup>();
            if (nativeGrid != null)
                return Mathf.Abs(rect.rect.width - nativeGrid.cellSize.x) < .05f &&
                    Mathf.Abs(rect.rect.height - nativeGrid.cellSize.y) < .05f;
            var native = view.itemgroup_save.GetCells().FirstOrDefault();
            return native != null && UiLayoutDiagnostics.SameSize(rect, native.transform);
        }

        // Fit to the original text rectangle without reducing its font size or accepting rich-text tags.
        private static string Ellipsize(Text text, string value, float? lineWidth = null)
        {
            string clean = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            float available = lineWidth ?? text.rectTransform.rect.width;
            if (available <= 0 || string.IsNullOrEmpty(clean)) return clean;
            var settings = text.GetGenerationSettings(new Vector2(available, text.rectTransform.rect.height));
            float scale = text.pixelsPerUnit;
            if (text.cachedTextGeneratorForLayout.GetPreferredWidth(clean, settings) / scale <= available) return clean;
            var elements = StringInfo.GetTextElementEnumerator(clean);
            string fit = "";
            while (elements.MoveNext())
            {
                string candidate = fit + elements.GetTextElement();
                if (text.cachedTextGeneratorForLayout.GetPreferredWidth(candidate + "…", settings) / scale > available) break;
                fit = candidate;
            }
            return fit + "…";
        }
    }
}
