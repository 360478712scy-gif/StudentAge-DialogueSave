using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using View.Evt;
using View.Main;

namespace StudentAgeDialogueSave.UI
{
    public enum DialogueHotkeyAction { Save, Load, QuickSave, QuickLoad }

    public sealed class DialogueHotkeyBinding
    {
        public DialogueHotkeyAction Action { get; internal set; }
        public Key Key { get; internal set; }
        public Key RequestedKey { get; internal set; }
        public string Path { get; internal set; }
        public string Label { get; internal set; }
        public string AtlasUrl { get; internal set; }
        public string ConflictReason { get; internal set; }
        public bool RenderLabelOnKeycap { get; internal set; }
    }

    // Owns only its four InputActions. Never mutates Cfg, native bindings, or an action map.
    // Native 105/119 must be consumed (not dispatched again) by the dialogue controller.
    public sealed class DialogueHotkeyBindings : IDisposable
    {
        readonly Func<bool> canDispatch;
        readonly Action<DialogueHotkeyAction> execute;
        readonly Action<string> log;
        readonly Dictionary<DialogueHotkeyAction, DialogueHotkeyBinding> bindings = new Dictionary<DialogueHotkeyAction, DialogueHotkeyBinding>();
        readonly Dictionary<DialogueHotkeyAction, InputAction> inputs = new Dictionary<DialogueHotkeyAction, InputAction>();
        static readonly Key[] Defaults = { Key.S, Key.L, Key.H, Key.J };
        static readonly Key[][] Preferred =
        {
            new[] { Key.S, Key.K }, new[] { Key.L, Key.O }, new[] { Key.H, Key.G }, new[] { Key.J, Key.U }
        };
        static readonly Key[] Alternatives =
        {
            Key.K, Key.O, Key.G, Key.U, Key.F6, Key.F7, Key.F9, Key.F10, Key.F11, Key.F12,
            Key.Q, Key.E, Key.R, Key.T, Key.Y, Key.I, Key.P, Key.Z, Key.X, Key.C, Key.V, Key.B, Key.N, Key.M
        };
        string fingerprint;
        bool disposed, enabled;
        float nextRefresh;
        int lastDispatchFrame = -1;
        public event Action BindingsChanged;

        public DialogueHotkeyBindings(Func<bool> canDispatch, Action<DialogueHotkeyAction> execute, Action<string> log)
        {
            this.canDispatch = canDispatch ?? throw new ArgumentNullException(nameof(canDispatch));
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.log = log ?? (_ => { });
        }

        public DialogueHotkeyBinding GetBinding(DialogueHotkeyAction action)
        {
            if (!disposed && bindings.Count == 0) RefreshBindings();
            return bindings.TryGetValue(action, out var binding) ? binding : null;
        }

        public void Tick()
        {
            if (disposed) return;
            if (Time.realtimeSinceStartup >= nextRefresh)
            {
                nextRefresh = Time.realtimeSinceStartup + .5f;
                RefreshBindings();
            }
            bool active = inputs.Count == 4 && canDispatch() && IsInputScope();
            if (active == enabled) return;
            enabled = active;
            foreach (var action in inputs.Values) { if (active) action.Enable(); else action.Disable(); }
        }

        public static bool IsInputScope()
        {
            if (!Application.isFocused || Keyboard.current == null || EventSystem.current == null ||
                UIMgr.IsShowingMask() || DebugMgr.IsShowing()) return false;
            var talk = UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            if (talk == null || talk.viewState != ViewState.Opened || talk.gameObject == null) return false;
            // Match native dialogue navigation's exclusion of passive Side views.
            // Hover views are already excluded by UIMgr; modal/guide views still block.
            BaseView top = UIMgr.GetTopView(ViewType.Side);
            if (top != talk)
            {
                var pause = top as EntryView;
                if (pause == null || pause.parms == null || pause.parms.Length == 0 || !(pause.parms[0] is bool) || !(bool)pause.parms[0])
                    return false;
            }
            GameObject selected = EventSystem.current.currentSelectedGameObject;
            if (selected != null)
            {
                var text = selected.GetComponentInParent<TMP_InputField>();
                var legacy = selected.GetComponentInParent<InputField>();
                if ((text != null && text.isFocused) || (legacy != null && legacy.isFocused)) return false;
            }
            var keyboard = Keyboard.current;
            if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed ||
                keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed) return false;
            InputActionAsset native = NativeAsset();
            return native != null && native.FindActionMap("UI", false)?.enabled == true;
        }

        void RefreshBindings()
        {
            InputActionAsset native = NativeAsset();
            if (native == null || Cfg.HotKeyCfgMap == null) return;
            var reserved = new Dictionary<Key, string>();
            var signatures = new List<string>();
            InputActionMap ui = native.FindActionMap("UI", false);
            if (ui == null) return;
            foreach (InputAction action in ui.actions) Collect(action, reserved, signatures);
            // The UI module may use its own navigation/submit actions rather than Control's map.
            var module = EventSystem.current == null ? null : EventSystem.current.GetComponent<InputSystemUIInputModule>();
            if (module != null)
            {
                if (module.move != null) Collect(module.move.action, reserved, signatures);
                if (module.submit != null) Collect(module.submit.action, reserved, signatures);
                if (module.cancel != null) Collect(module.cancel.action, reserved, signatures);
            }
            foreach (var key in Cfg.HotKeyCfgMap.Values.OrderBy(k => k.id))
                signatures.Add("icon:" + key.id + ":" + key.path + ":" + key.icon);
            string signature = string.Join("\n", signatures.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            if (signature == fingerprint) return;
            var next = new Dictionary<DialogueHotkeyAction, DialogueHotkeyBinding>();
            var taken = new HashSet<Key>();
            for (int i = 0; i < 4; i++)
            {
                // Preserve each other action's requested key when choosing a fallback.
                Key[] candidates = Preferred[i].Concat(Alternatives).Distinct().ToArray();
                Key key = candidates.FirstOrDefault(k => !reserved.ContainsKey(k) && !taken.Contains(k) &&
                    (!Defaults.Contains(k) || k == Defaults[i]));
                if (key == Key.None)
                {
                    log("没有可避免冲突的对话快捷键，四项操作仍可通过按钮使用。");
                    return;
                }
                taken.Add(key);
                string path = "<Keyboard>/" + key.ToString().ToLowerInvariant();
                HotKeyCfg icon = Cfg.HotKeyCfgMap.Values.FirstOrDefault(c => string.Equals(c.path, path, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.icon));
                string conflict = reserved.TryGetValue(Defaults[i], out var owner) ? owner : null;
                next[(DialogueHotkeyAction)i] = new DialogueHotkeyBinding
                {
                    Action = (DialogueHotkeyAction)i, RequestedKey = Defaults[i], Key = key, Path = path,
                    Label = key.ToString().ToUpperInvariant(), AtlasUrl = icon == null ? "common5/img_key_bg" : icon.icon,
                    RenderLabelOnKeycap = icon == null, ConflictReason = conflict
                };
            }
            foreach (var input in inputs.Values) input.Dispose();
            inputs.Clear(); bindings.Clear(); enabled = false;
            foreach (var item in next)
            {
                DialogueHotkeyAction action = item.Key;
                bindings.Add(action, item.Value);
                // Press-only action; no repeat and no stale key-release callback after a menu opens.
                var input = new InputAction("DialogueSave." + action, InputActionType.Button, item.Value.Path);
                input.performed += _ => Dispatch(action);
                inputs.Add(action, input);
                log("对话快捷键 " + action + "=" + item.Value.Label + (item.Value.Key == item.Value.RequestedKey ? "" :
                    "；" + item.Value.RequestedKey + " 与 " + (item.Value.ConflictReason ?? "另一项操作") + " 冲突，已避让。"));
            }
            fingerprint = signature;
            BindingsChanged?.Invoke();
        }

        void Dispatch(DialogueHotkeyAction action)
        {
            if (disposed || !enabled || lastDispatchFrame == Time.frameCount || !IsInputScope() || !canDispatch()) return;
            lastDispatchFrame = Time.frameCount;
            execute(action);
        }

        static void Collect(InputAction action, Dictionary<Key, string> reserved, List<string> signatures)
        {
            if (action == null) return;
            foreach (InputBinding binding in action.bindings)
            {
                string path = binding.effectivePath;
                signatures.Add(action.actionMap?.name + "/" + action.name + ":" + path);
                if (binding.isComposite || string.IsNullOrEmpty(path)) continue;
                const string prefix = "<Keyboard>/";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse(path.Substring(prefix.Length), true, out Key key) && key != Key.None)
                    reserved[key] = (action.actionMap?.name ?? "UI") + "/" + action.name;
            }
            // Resolved controls additionally cover usage-based bindings such as */{Submit}.
            foreach (var control in action.controls)
                if (control is UnityEngine.InputSystem.Controls.KeyControl key)
                {
                    reserved[key.keyCode] = (action.actionMap?.name ?? "UI") + "/" + action.name;
                    signatures.Add("resolved:" + action.actionMap?.name + "/" + action.name + ":" + key.keyCode);
                }
        }

        static InputActionAsset NativeAsset()
        {
            object control = AccessTools.Field(typeof(Control), "ins")?.GetValue(null);
            if (control == null) return null;
            object input = AccessTools.Field(typeof(Control), "controls")?.GetValue(control);
            return input?.GetType().GetProperty("asset", BindingFlags.Public | BindingFlags.Instance)?.GetValue(input, null) as InputActionAsset;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; enabled = false;
            foreach (var input in inputs.Values) input.Dispose();
            inputs.Clear(); bindings.Clear(); BindingsChanged = null;
        }
    }
}
