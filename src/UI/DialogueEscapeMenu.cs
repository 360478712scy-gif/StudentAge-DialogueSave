using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Main;

namespace StudentAgeDialogueSave.UI
{
    // Only native save/load captions are added to the dialogue pause menu; no key caps or quick actions.
    internal sealed class DialogueEscapeMenu : IDisposable
    {
        readonly EntryView view;
        readonly List<GameObject> owned = new List<GameObject>();
        readonly Dictionary<GameObject, bool> hidden = new Dictionary<GameObject, bool>();
        readonly List<UIButton> buttons = new List<UIButton>();
        readonly JObject originalGroupGeometry;
        bool disposed;

        internal static bool IsDialogueMenu(EntryView entry)
        {
            return entry != null && entry.gameObject != null && entry.viewState == ViewState.Opened &&
                entry.gameObject.activeInHierarchy && (bool)AccessTools.Field(typeof(EntryView), "isPause").GetValue(entry);
        }

        internal DialogueEscapeMenu(EntryView view, Action<DialogueHotkeyAction> execute)
        {
            this.view = view;
            originalGroupGeometry = GroupGeometry(view.group_btn);
            try
            {
                Hide(view.btn_save.gameObject); Hide(view.btn_load.gameObject);
                int index = view.btn_back.transform.GetSiblingIndex() + 1;
                foreach (var action in new[] { DialogueHotkeyAction.Save, DialogueHotkeyAction.Load })
                {
                    var source = action == DialogueHotkeyAction.Save ? view.btn_save : view.btn_load;
                    var button = NativeUiCloner.CloneButton(source, "DialogueSave.Menu." + action, () => execute(action));
                    owned.Add(button.gameObject); buttons.Add(button);
                    button.transform.SetSiblingIndex(index++);
                    button.interactable = true;
                    foreach (var description in button.gameObject.GetComponentsInChildren<Components.Description>(true))
                        description.getDescription = _ => null;
                }
                LayoutRebuilder.MarkLayoutForRebuild(view.group_btn);
            }
            catch { Dispose(); throw; }
        }
        IEnumerable<RectTransform> VisibleButtons()
        {
            return view.group_btn.GetComponentsInChildren<Button>(false)
                .Where(b => b.enabled && b.transform.parent == view.group_btn).Select(b => (RectTransform)b.transform);
        }
        void Hide(GameObject obj) { hidden[obj] = obj.activeSelf; obj.SetActive(false); }
        internal JObject CaptureLayoutReport()
        {
            return new JObject
            {
                ["dialoguePauseMenu"] = IsDialogueMenu(view),
                ["originalGroupGeometry"] = originalGroupGeometry.DeepClone(),
                ["groupGeometry"] = GroupGeometry(view.group_btn),
                ["groupGeometryUnchanged"] = JToken.DeepEquals(originalGroupGeometry, GroupGeometry(view.group_btn)),
                ["buttons"] = new JArray(buttons.Where(b => b.gameObject != null).Select(b => new JObject
                {
                    ["name"] = b.gameObject.name, ["rect"] = UiLayoutDiagnostics.Rect(b.transform),
                    ["sameNativeSize"] = UiLayoutDiagnostics.SameSize(b.transform, view.btn_save.transform),
                    ["graphics"] = UiLayoutDiagnostics.Graphics(b.transform)
                })),
                ["allButtons"] = new JArray(VisibleButtons().Select(UiLayoutDiagnostics.Rect)),
                ["noOverlap"] = UiLayoutDiagnostics.DoNotOverlap(VisibleButtons()),
                ["hasNoKeyCaps"] = view.group_btn.GetComponentsInChildren<Transform>(true).All(t => !t.name.StartsWith("DialogueSave.MenuKey.", StringComparison.Ordinal))
            };
        }
        private static JObject GroupGeometry(RectTransform rect)
        {
            var layout = rect.GetComponent<HorizontalOrVerticalLayoutGroup>();
            return new JObject
            {
                ["anchorMin"] = new JArray(rect.anchorMin.x, rect.anchorMin.y),
                ["anchorMax"] = new JArray(rect.anchorMax.x, rect.anchorMax.y),
                ["pivot"] = new JArray(rect.pivot.x, rect.pivot.y),
                ["anchoredPosition"] = new JArray(rect.anchoredPosition3D.x, rect.anchoredPosition3D.y, rect.anchoredPosition3D.z),
                ["sizeDelta"] = new JArray(rect.sizeDelta.x, rect.sizeDelta.y),
                ["localScale"] = new JArray(rect.localScale.x, rect.localScale.y, rect.localScale.z),
                ["localRotation"] = new JArray(rect.localRotation.x, rect.localRotation.y, rect.localRotation.z, rect.localRotation.w),
                ["layout"] = layout == null ? JValue.CreateNull() : (JToken)new JObject
                {
                    ["type"] = layout.GetType().Name, ["enabled"] = layout.enabled,
                    ["padding"] = new JArray(layout.padding.left, layout.padding.right, layout.padding.top, layout.padding.bottom),
                    ["alignment"] = layout.childAlignment.ToString(), ["spacing"] = layout.spacing,
                    ["controlWidth"] = layout.childControlWidth, ["controlHeight"] = layout.childControlHeight,
                    ["expandWidth"] = layout.childForceExpandWidth, ["expandHeight"] = layout.childForceExpandHeight,
                    ["scaleWidth"] = layout.childScaleWidth, ["scaleHeight"] = layout.childScaleHeight
                }
            };
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var obj in owned) NativeUiCloner.Remove(obj);
            foreach (var pair in hidden) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            if (view.group_btn != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(view.group_btn);
            }
        }
    }
}
