using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Config;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using View.Evt;
using View.Main;

namespace StudentAgeDialogueSave.GameIntegration
{
    // A deliberately finite native continuation registry. A saved method token is an
    // identity check, never permission to execute an arbitrary method from a save.
    // Not yet adapted: consumed ItemData/useEffector callbacks, intent reward effectors,
    // compound love-action closures, arbitrary other UI/minigame targets, third-party
    // callbacks, and base windows outside home/map/site/NPC. Those remain explicit
    // capture failures; this registry does not claim universal dialogue restoration.
    public static class DialogueContinuation
    {
        sealed class Rule
        {
            internal string Key, TypeName, MethodName;
            internal string[] Fields;
            internal string[] IgnoredDelegateFields;
            internal bool StudyTarget, SceneTarget;
            internal MethodInfo Method
            {
                get
                {
                    if (GameAssembly.ManifestModule.ModuleVersionId != AuditedGameModule)
                        throw new InvalidDataException("此游戏版本的原生剧情返回流程尚未审查。");
                    Type type = GameAssembly.GetType(TypeName, false);
                    MethodInfo method = type == null ? null : type.GetMethod(MethodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (method == null || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                        throw new InvalidDataException("当前游戏版本的剧情返回方法已改变：" + Key);
                    if (!StudyTarget && !SceneTarget)
                    {
                        string[] ignored = IgnoredDelegateFields ?? new string[0];
                        string[] actual = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .Select(f => f.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                        if (!actual.SequenceEqual(Fields.Concat(ignored).OrderBy(x => x, StringComparer.Ordinal)))
                            throw new InvalidDataException("原生剧情闭包字段已改变，需要更新适配：" + Key);
                        foreach (string name in ignored)
                            if (GetField(type, name).FieldType != typeof(Action))
                                throw new InvalidDataException("原生剧情辅助委托字段已改变：" + Key);
                    }
                    return method;
                }
            }
        }

        static readonly Assembly GameAssembly = typeof(CommonEvtMgr).Assembly;
        static readonly Guid AuditedGameModule = new Guid("e0298a66-0f75-4b24-b030-dcc6a6778c63");
        static readonly Rule[] Rules =
        {
            new Rule { Key = "exam-result", TypeName = "StudyData", MethodName = "ShowExamResultComp", Fields = new string[0], StudyTarget = true },
            // close is only read by the alternate minigame wrapper b__1; <>9__2 is
            // a lazily created reward-dialogue delegate cache. b__0 rebuilds that cache.
            new Rule { Key = "action-result", TypeName = "ActionData+<>c__DisplayClass22_0", MethodName = "<HelpAction>b__0", Fields = new[] { "cfg", "rate", "_data", "<>4__this" }, IgnoredDelegateFields = new[] { "close", "<>9__2" } },
            new Rule { Key = "social-game-start", TypeName = "MiniGameData+<>c__DisplayClass4_0", MethodName = "<SocialGame>b__0", Fields = new[] { "game" } },
            new Rule { Key = "social-game-finish", TypeName = "MiniGameData+<>c__DisplayClass5_0", MethodName = "<EndGame>b__0", Fields = new[] { "game", "_isWin", "_selectId" } },
            new Rule { Key = "negotiation-start", TypeName = "NegotiationData+<>c__DisplayClass16_0", MethodName = "<Negotiate>b__0", Fields = new[] { "_id" } },
            new Rule { Key = "egame-refresh", TypeName = "EGameData+<>c", MethodName = "<RequestEGameStuck>b__17_0", Fields = new string[0] },
            new Rule { Key = "intent-fail-refresh", TypeName = "IntentData+<>c", MethodName = "<GetIntentFail>b__17_0", Fields = new string[0] },
            new Rule { Key = "home-to-map", TypeName = "View.Main.MainView+<>c", MethodName = "<OnClickMap>b__26_0", Fields = new string[0] },
            new Rule { Key = "leave-site", TypeName = "Sdk.BaseView", MethodName = "CloseView", Fields = new[] { "mapId" }, SceneTarget = true }
        };

        sealed class SceneCloseContinuation
        {
            internal readonly MapSceneView View;
            internal SceneCloseContinuation(MapSceneView view) { View = view; }
            // BaseView.CloseView is exactly UIMgr.CloseView(this). Calling the derived
            // override would start the same leave-site event again instead of returning.
            internal void Invoke() { UIMgr.CloseView(View); }
        }

        public static bool IsBaseView(Type type)
        {
            return type == typeof(MainView) || type == typeof(MapView) ||
                type == typeof(MapSceneView) || type == typeof(MapRoleView);
        }

        public static JObject Capture(NewTalkView view)
        {
            if (view == null) throw new InvalidOperationException("缺少正在显示的对话。");
            var roots = new JArray();
            AddRoot<MainView>(roots, "home");
            AddRoot<MapView>(roots, "map");
            AddRoot<MapSceneView>(roots, "site");
            AddRoot<MapRoleView>(roots, "npc");
            var result = new JObject
            {
                ["format"] = 1,
                ["mvid"] = GameAssembly.ManifestModule.ModuleVersionId.ToString("D"),
                ["views"] = roots,
                ["callback"] = CaptureCallback(view.callback)
            };
            Validate(result);
            return result;
        }

        static void AddRoot<T>(JArray roots, string kind) where T : BaseView, new()
        {
            var view = UIMgr.GetView<T>(false) as BaseView;
            if (view == null) return;
            if (view.viewState == ViewState.Loading || view.viewState == ViewState.Loaded)
                throw new InvalidOperationException("场景窗口正在切换，请等待它打开完成。");
            if (view.viewState != ViewState.Opened) return;
            var args = new JArray();
            if (kind == "site")
            {
                int map = (int)AccessTools.Field(typeof(MapSceneView), "mapId").GetValue(view);
                bool other = (bool)AccessTools.Field(typeof(MapSceneView), "isOtherBg").GetValue(view);
                args.Add(map);
                args.Add(other ? Cfg.MapCfgMap[map].bg2 : Cfg.MapCfgMap[map].bg);
            }
            else if (kind == "npc")
            {
                args.Add((int)AccessTools.Field(typeof(MapRoleView), "npcId").GetValue(view));
                args.Add((int)AccessTools.Field(typeof(MapRoleView), "bgId").GetValue(view));
                args.Add((int)AccessTools.Field(typeof(MapRoleView), "mapId").GetValue(view));
                args.Add(view.parms != null && view.parms.Length > 3 && (bool)view.parms[3]);
            }
            roots.Add(new JObject { ["kind"] = kind, ["args"] = args });
        }

        public static JToken CaptureCallback(Action callback)
        {
            if (callback == null) return JValue.CreateNull();
            if (callback.GetInvocationList().Length == 1 && callback.Target is SceneCloseContinuation restored &&
                callback.Method == typeof(SceneCloseContinuation).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic))
                return CaptureSceneClose(restored.View);
            if (callback.GetInvocationList().Length != 1 || callback.Method.Module.Assembly != GameAssembly)
                throw new InvalidOperationException("此对话包含尚未适配的插件或组合返回流程。");
            Rule rule = Rules.FirstOrDefault(r => callback.Method.DeclaringType.FullName == r.TypeName && callback.Method.Name == r.MethodName);
            if (rule == null) throw new InvalidOperationException("此原生返回流程尚未适配：" + callback.Method.DeclaringType.FullName + "." + callback.Method.Name);
            MethodInfo method = rule.Method;
            if (method.MetadataToken != callback.Method.MetadataToken || callback.Target == null)
                throw new InvalidDataException("原生返回流程身份不匹配。");
            if (rule.SceneTarget)
            {
                var scene = callback.Target as MapSceneView;
                if (scene == null) throw new InvalidOperationException("此关闭窗口返回流程尚未适配。");
                return CaptureSceneClose(scene);
            }
            if (rule.StudyTarget && !ReferenceEquals(callback.Target, Singleton<RoleMgr>.Ins.GetStudyData(false)))
                throw new InvalidOperationException("考试返回流程没有绑定到当前世界。");
            if (rule.IgnoredDelegateFields != null)
            {
                foreach (string name in rule.IgnoredDelegateFields)
                {
                    var cached = GetField(method.DeclaringType, name).GetValue(callback.Target) as Action;
                    if (cached == null) continue;
                    string expected = name == "close" ? "<HelpAction>b__0" : "<HelpAction>b__2";
                    if (cached.GetInvocationList().Length != 1 || !ReferenceEquals(cached.Target, callback.Target) ||
                        cached.Method.DeclaringType != method.DeclaringType || cached.Method.Name != expected)
                        throw new InvalidOperationException("行动后续委托已被其他代码替换，不能安全省略。");
                }
            }
            var fields = new JObject();
            foreach (string name in rule.Fields)
            {
                FieldInfo field = GetField(method.DeclaringType, name);
                fields[name] = WriteValue(field.FieldType, field.GetValue(callback.Target));
            }
            return new JObject { ["rule"] = rule.Key, ["token"] = method.MetadataToken, ["fields"] = fields };
        }

        static JObject CaptureSceneClose(MapSceneView scene)
        {
            if (scene == null || !ReferenceEquals(scene, UIMgr.GetView<MapSceneView>(false)))
                throw new InvalidOperationException("离开场景返回流程未绑定当前场景。");
            int mapId = (int)GetField(typeof(MapSceneView), "mapId").GetValue(scene);
            return new JObject { ["rule"] = "leave-site", ["token"] = FindRule("leave-site").Method.MetadataToken,
                ["fields"] = new JObject { ["mapId"] = mapId } };
        }

        public static void Validate(JObject value)
        {
            Shape(value, "format", "mvid", "views", "callback");
            if (Integer(value["format"]) != 1 || (string)value["mvid"] != GameAssembly.ManifestModule.ModuleVersionId.ToString("D"))
                throw new InvalidDataException("此剧情返回流程需要保存时的游戏版本。");
            var views = value["views"] as JArray;
            if (views == null || views.Count < 1 || views.Count > 4) throw new InvalidDataException("缺少可恢复的原场景。");
            int prior = -1;
            foreach (JObject item in views)
            {
                Shape(item, "kind", "args");
                int index = Array.IndexOf(new[] { "home", "map", "site", "npc" }, (string)item["kind"]);
                if (index <= prior) throw new InvalidDataException("场景层级无效。");
                prior = index;
                var args = item["args"] as JArray;
                int length = index < 2 ? 0 : index == 2 ? 2 : 4;
                if (args == null || args.Count != length) throw new InvalidDataException("场景参数无效。");
                if (index == 2 && (!Cfg.MapCfgMap.ContainsKey(Integer(args[0])) || !Cfg.BgCfgMap.ContainsKey(Integer(args[1]))))
                    throw new InvalidDataException("保存的地图场景配置已缺失。");
                if (index == 3 && (!Cfg.PersonCfgMap.ContainsKey(Integer(args[0])) ||
                    !Cfg.MapCfgMap.ContainsKey(Integer(args[2])) || args[3].Type != JTokenType.Boolean ||
                    (Integer(args[1]) > 0 && !Cfg.BgCfgMap.ContainsKey(Integer(args[1])))))
                    throw new InvalidDataException("保存的 NPC 场景配置已缺失。");
            }
            ValidateCallback(value["callback"]);
            if (value["callback"] is JObject savedCallback && (string)savedCallback["rule"] == "leave-site" &&
                !views.OfType<JObject>().Any(v => (string)v["kind"] == "site" &&
                    Integer(v["args"][0]) == Integer(savedCallback["fields"]["mapId"])))
                throw new InvalidDataException("离开场景回调缺少对应的原场景。");
        }

        public static void ValidateCallback(JToken callback)
        {
            if (callback != null && callback.Type == JTokenType.Null) return;
            JObject value = callback as JObject;
            Shape(value, "rule", "token", "fields");
            Rule rule = FindRule((string)value["rule"]);
            MethodInfo method = rule.Method;
            if (Integer(value["token"]) != method.MetadataToken) throw new InvalidDataException("剧情返回方法与当前版本不匹配。");
            var fields = value["fields"] as JObject;
            Shape(fields, rule.Fields);
            if (rule.SceneTarget)
            {
                if (!Cfg.MapCfgMap.ContainsKey(Integer(fields["mapId"]))) throw new InvalidDataException("返回场景配置缺失。");
                return;
            }
            foreach (string name in rule.Fields) ReadValue(GetField(method.DeclaringType, name).FieldType, fields[name], false);
            if (rule.Key == "negotiation-start" && !Cfg.NegotiationCfgMap.ContainsKey(Integer(fields["_id"])))
                throw new InvalidDataException("谈判返回流程配置缺失。");
            if (rule.Key == "action-result" && Integer(fields["cfg"]) != Integer(fields["_data"]))
                throw new InvalidDataException("行动返回流程配置与世界对象不一致。");
        }

        // Called after decoding the detached world but before replacing live managers.
        public static void ValidateWorld(JObject value, IEnumerable<ISaveLoadValue> incoming)
        {
            Validate(value);
            ValidateCallbackWorld(value["callback"], incoming);
        }

        public static void ValidateCallbackWorld(JToken callback, IEnumerable<ISaveLoadValue> incoming)
        {
            ValidateCallback(callback);
            if (callback.Type == JTokenType.Null) return;
            var models = incoming.ToList();
            var value = (JObject)callback;
            Rule rule = FindRule((string)value["rule"]);
            if (rule.SceneTarget) return;
            object role = models.OfType<RoleModel>().Single();
            if (rule.StudyTarget && GetField(role.GetType(), "studyData").GetValue(role) == null)
                throw new InvalidDataException("存档中缺少考试返回对象。");
            foreach (string name in rule.Fields)
            {
                Type type = GetField(rule.Method.DeclaringType, name).FieldType;
                if (type == typeof(ActionData) || type == typeof(ActionSubData))
                {
                    var action = GetField(role.GetType(), "actionData").GetValue(role) as ActionData;
                    if (action == null || (type == typeof(ActionSubData) && action.GetAction(Integer(value["fields"][name])) == null))
                        throw new InvalidDataException("存档中缺少行动返回对象。");
                }
                else if (type == typeof(MiniGameSubData))
                {
                    object func = models.OfType<FuncModel>().Single();
                    object data = GetField(func.GetType(), "miniGameData").GetValue(func);
                    var games = data == null ? null : GetField(typeof(MiniGameData), "games").GetValue(data) as IDictionary;
                    if (games == null || !games.Contains(Integer(value["fields"][name])))
                        throw new InvalidDataException("存档中缺少社交小游戏返回对象。");
                }
            }
        }

        // The adapter owns cancellation, rollback, input shielding and restoration generation.
        // Wait for each native parent so child OnOpen observes the same scene hierarchy.
        public static async Task RestoreBaseViews(JObject value, Func<Task> nextFrame, Func<bool> stillCurrent)
        {
            Validate(value);
            foreach (JObject item in (JArray)value["views"])
            {
                if (!stillCurrent()) throw new OperationCanceledException("剧情恢复已取消。");
                string kind = (string)item["kind"];
                var args = (JArray)item["args"];
                if (kind == "home") UIMgr.OpenView<MainView>();
                else if (kind == "map") UIMgr.OpenView<MapView>();
                else if (kind == "site") UIMgr.OpenView<MapSceneView>(UILayerType.None, null, new object[] { Integer(args[0]), Integer(args[1]) });
                else UIMgr.OpenView<MapRoleView>(UILayerType.None, null, new object[] { Integer(args[0]), Integer(args[1]), Integer(args[2]), (bool)args[3] });
                float deadline = Time.realtimeSinceStartup + 20;
                while (!Opened(kind))
                {
                    if (!stillCurrent()) throw new OperationCanceledException("剧情恢复已取消。");
                    if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("恢复原场景超时：" + kind);
                    await nextFrame();
                }
            }
            if (!stillCurrent()) throw new OperationCanceledException("剧情恢复已取消。");
        }

        public static bool BaseViewsReady(JObject value)
        {
            return value != null && value["views"] is JArray views &&
                views.Count > 0 && views.OfType<JObject>().All(v => Opened((string)v["kind"]));
        }

        static bool Opened(string kind)
        {
            if (kind == "home") return UIMgr.IsViewOpened<MainView>();
            if (kind == "map") return UIMgr.IsViewOpened<MapView>();
            if (kind == "site") return UIMgr.IsViewOpened<MapSceneView>();
            return UIMgr.IsViewOpened<MapRoleView>();
        }

        public static void Bind(NewTalkView view, JObject value)
        {
            Validate(value);
            view.callback = RestoreCallback(value["callback"]);
        }

        public static Action RestoreCallback(JToken callback)
        {
            ValidateCallback(callback);
            if (callback.Type == JTokenType.Null) return null;
            var value = (JObject)callback;
            Rule rule = FindRule((string)value["rule"]);
            MethodInfo method = rule.Method;
            if (rule.SceneTarget)
            {
                var scene = UIMgr.GetView<MapSceneView>(false) as MapSceneView;
                if (scene == null || (int)GetField(typeof(MapSceneView), "mapId").GetValue(scene) != Integer(value["fields"]["mapId"]))
                    throw new InvalidDataException("离开场景返回窗口尚未恢复。");
                return new SceneCloseContinuation(scene).Invoke;
            }
            object target;
            if (rule.StudyTarget) target = Singleton<RoleMgr>.Ins.GetStudyData(false);
            else
            {
                // Only concrete audited native target types can reach here.
                target = FormatterServices.GetUninitializedObject(method.DeclaringType);
                foreach (string name in rule.Fields)
                {
                    FieldInfo field = GetField(method.DeclaringType, name);
                    field.SetValue(target, ReadValue(field.FieldType, value["fields"][name], true));
                }
            }
            if (target == null) throw new InvalidDataException("存档世界缺少返回流程所需的数据。");
            return (Action)Delegate.CreateDelegate(typeof(Action), target, method);
        }

        static JToken WriteValue(Type type, object value)
        {
            if (type == typeof(int) || type == typeof(bool) || type == typeof(float)) return new JValue(value);
            if (value == null) throw new InvalidOperationException("原生返回流程引用了空对象。");
            if (type == typeof(ActionCfg))
            {
                int id = ((ActionCfg)value).id;
                if (!Cfg.ActionCfgMap.ContainsKey(id) || !ReferenceEquals(Cfg.ActionCfgMap[id], value)) throw new InvalidOperationException("行动配置不是当前原生配置。");
                return new JValue(id);
            }
            if (type == typeof(ActionData))
            {
                if (!ReferenceEquals(value, Singleton<RoleMgr>.Ins.GetActionData())) throw new InvalidOperationException("行动返回流程不属于当前世界。");
                return new JValue("world-action");
            }
            if (type == typeof(ActionSubData) || type == typeof(MiniGameSubData))
            {
                int id = (int)GetField(type, "id").GetValue(value);
                if (!ReferenceEquals(value, ResolveWorld(type, id))) throw new InvalidOperationException("返回流程引用的世界对象已经移除。");
                return new JValue(id);
            }
            throw new InvalidOperationException("返回流程含未审查字段类型：" + type.FullName);
        }

        static object ReadValue(Type type, JToken value, bool bind)
        {
            if (type == typeof(int)) return Integer(value);
            if (type == typeof(bool))
            {
                if (value == null || value.Type != JTokenType.Boolean) throw new InvalidDataException("返回流程布尔参数无效。");
                return (bool)value;
            }
            if (type == typeof(float))
            {
                if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) throw new InvalidDataException("返回流程倍率无效。");
                float number = (float)value;
                if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidDataException("返回流程倍率无效。");
                return number;
            }
            if (type == typeof(ActionCfg))
            {
                int id = Integer(value);
                if (!Cfg.ActionCfgMap.ContainsKey(id)) throw new InvalidDataException("返回流程行动配置缺失。");
                return bind ? Cfg.ActionCfgMap[id] : null;
            }
            if (type == typeof(ActionData))
            {
                if (value == null || value.Type != JTokenType.String || (string)value != "world-action") throw new InvalidDataException("行动世界引用无效。");
                return bind ? Singleton<RoleMgr>.Ins.GetActionData() : null;
            }
            if (type == typeof(ActionSubData) || type == typeof(MiniGameSubData))
            {
                int id = Integer(value);
                if (id <= 0 || (type == typeof(ActionSubData) ? !Cfg.ActionCfgMap.ContainsKey(id) : !Cfg.MinigameCfgMap.ContainsKey(id)))
                    throw new InvalidDataException("剧情返回对象标识无效。");
                return bind ? ResolveWorld(type, id) : null;
            }
            throw new InvalidDataException("返回流程字段类型不在白名单中。");
        }

        static object ResolveWorld(Type type, int id)
        {
            object value = null;
            if (type == typeof(ActionSubData)) value = Singleton<RoleMgr>.Ins.GetActionData().GetAction(id);
            else
            {
                var model = AccessTools.Field(typeof(FuncMgr), "model").GetValue(Singleton<FuncMgr>.Ins);
                var data = GetField(model.GetType(), "miniGameData").GetValue(model);
                var games = data == null ? null : GetField(typeof(MiniGameData), "games").GetValue(data) as IDictionary;
                if (games != null && games.Contains(id)) value = games[id];
            }
            if (value == null) throw new InvalidDataException("存档世界缺少剧情返回对象：" + type.Name + "/" + id);
            return value;
        }

        static Rule FindRule(string key)
        {
            Rule rule = Rules.FirstOrDefault(r => r.Key == key);
            if (rule == null) throw new InvalidDataException("剧情返回类型不在白名单中。");
            return rule;
        }
        static FieldInfo GetField(Type type, string name)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic) throw new InvalidDataException("原生返回字段已改变：" + type.Name + "." + name);
            return field;
        }
        static int Integer(JToken value)
        {
            if (value == null || value.Type != JTokenType.Integer) throw new InvalidDataException("剧情参数不是整数。");
            try { return (int)value; } catch (Exception ex) { throw new InvalidDataException("剧情参数超出范围。", ex); }
        }
        static void Shape(JObject value, params string[] fields)
        {
            if (value == null || !value.Properties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(fields.OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidDataException("剧情返回数据结构无效。");
        }
    }
}
