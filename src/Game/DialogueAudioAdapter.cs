using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Config;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;

namespace StudentAgeDialogueSave.GameIntegration
{
    /// <summary>Only native, configuration-addressed BGM. Completed/spoken voice is never replayed.</summary>
    internal static class DialogueAudioAdapter
    {
        internal static bool CanCapture(out string reason)
        {
            reason = null;
            if (AudioMgr.Ins == null) { reason = "音频系统尚未准备好"; return false; }
            Channel bgm = AudioMgr.Ins.GetChannel(1);
            if (bgm == null || bgm.source == null) { reason = "背景音乐通道尚未准备好"; return false; }
            if (!KnownBgmCallback(bgm.callback, 0)) { reason = "背景音乐包含未适配的完成回调，暂不能保存"; return false; }
            // Positive fades only change volume. Negative fades invoke pauseCallback,
            // which may continue a pending operation and cannot be silently discarded.
            if (Field<float>(bgm, "fadeTime") < 0f) { reason = "请等待背景音乐切换完成后保存"; return false; }
            for (int i = 2; i <= 3; i++)
            {
                Channel channel = AudioMgr.Ins.GetChannel(i);
                if (channel != null && !KnownCallback(channel.callback, 0))
                { reason = "当前语音或音效包含未适配的完成回调"; return false; }
            }
            return true;
        }

        internal static JObject Capture()
        {
            if (!CanCapture(out string reason)) throw new InvalidOperationException(reason);
            Channel channel = AudioMgr.Ins.GetChannel(1);
            AudioSource source = channel.source;
            string continuation = "default";
            int group = (int)AudioMgrEx.curGroup;
            bool random = false;
            if (source.loop || channel.callback == null) continuation = "none";
            else if (channel.callback.Target is BgmContinuation own)
            {
                continuation = own.Kind;
                group = own.Group;
                random = own.Random;
            }
            else if (channel.callback.Method.Name.Contains("PlayGroupBgm"))
            {
                continuation = "group";
                if (channel.callback.Target != null)
                {
                    FieldInfo flag = AccessTools.Field(channel.callback.Target.GetType(), "_random");
                    if (flag == null) throw new InvalidOperationException("背景音乐列表回调布局已变化");
                    random = (bool)flag.GetValue(channel.callback.Target);
                }
            }
            return new JObject
            {
                ["playing"] = source.isPlaying,
                ["url"] = source.isPlaying ? AudioMgrEx.musicUrl : null,
                ["seconds"] = source.isPlaying ? source.time : 0f,
                ["volume"] = Field<float>(channel, "volumeScale"),
                ["loop"] = source.loop,
                ["continuation"] = continuation,
                ["group"] = group,
                ["random"] = random,
                ["playlistIndex"] = (int)AccessTools.Field(typeof(AudioMgrEx), "curPlayingMusicIdx").GetValue(null),
                ["forcePause"] = AudioMgrEx.forcePauseBgm
            };
        }

        internal static void Validate(JObject data)
        {
            if (data == null) throw new System.IO.InvalidDataException("缺少背景音乐状态");
            if (data["url"] != null && data["url"].Type != JTokenType.Null &&
                (data["url"].Type != JTokenType.String || data.Value<string>("url").Length > 4096))
                throw new System.IO.InvalidDataException("背景音乐资源标识无效");
            float time = data.Value<float>("seconds"), volume = data.Value<float>("volume");
            if (float.IsNaN(time) || float.IsInfinity(time) || time < 0 || time > 24 * 60 * 60 ||
                float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0 || volume > 10)
                throw new System.IO.InvalidDataException("背景音乐进度或音量无效");
            string kind = data.Value<string>("continuation");
            if (kind != "none" && kind != "default" && kind != "group") throw new System.IO.InvalidDataException("未知背景音乐继续方式");
            if (kind == "group" && !Enum.IsDefined(typeof(AudioGroupDefine), data.Value<int>("group")))
                throw new System.IO.InvalidDataException("未知背景音乐列表");
        }

        internal static async Task RestoreAsync(JObject data, Func<Task> nextFrame, Func<bool> stillCurrent)
        {
            Validate(data);
            AudioMgrEx.forcePauseBgm = data.Value<bool>("forcePause");
            AudioMgrEx.curGroup = (AudioGroupDefine)data.Value<int>("group");
            AccessTools.Field(typeof(AudioMgrEx), "curPlayingMusicIdx").SetValue(null, data.Value<int>("playlistIndex"));
            // Native StopAllMusic has already stopped old speech and scene sounds. Do not replay them.
            if (!data.Value<bool>("playing")) return;
            var continuation = new BgmContinuation(data.Value<string>("continuation"), data.Value<int>("group"), data.Value<bool>("random"));
            Action finished = continuation.Kind == "none" ? null : (Action)continuation.Invoke;
            Channel channel = AudioMgr.Ins == null ? null : AudioMgr.Ins.GetChannel(1);
            AudioClip loadedClip = null;
            bool completed = false;
            bool abandoned = false;
            string url = data.Value<string>("url");
            // Custom/removed music is optional presentation; never turn a restored world
            // into a failed load just because its BGM is unavailable on this machine.
            if (!KnownUrl(url)) { Silent(channel, "背景音乐资源未找到，已静音继续恢复对白：" + url); return; }
            if (channel == null || channel.source == null) { Silent(channel, "背景音乐通道未就绪，已静音继续恢复对白"); return; }
            try
            {
                if (!stillCurrent()) throw new OperationCanceledException("音频恢复事务已失效");
                try
                {
                    ResMgr.LoadAudioAsync(AudioMgrEx.FormatUrl(url), clip =>
                    {
                        if (abandoned || !stillCurrent()) { if (clip != null) ResMgr.Recycle(clip); return; }
                        loadedClip = clip;
                        completed = true;
                    });
                }
                catch (Exception error)
                {
                    Silent(channel, "背景音乐资源加载失败，已静音继续恢复对白：" + error.Message);
                    return;
                }
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!completed)
                {
                    if (!stillCurrent()) throw new OperationCanceledException("音频恢复事务已失效");
                    if (Time.realtimeSinceStartup > deadline) { Silent(channel, "背景音乐加载超时，已静音继续恢复对白"); return; }
                    await nextFrame();
                }
                if (!stillCurrent()) throw new OperationCanceledException("音频恢复事务已失效");
                if (loadedClip == null) { Silent(channel, "背景音乐资源加载失败，已静音继续恢复对白"); return; }
                // The native channel takes ownership only after the resource callback.
                channel.Play(loadedClip, data.Value<float>("volume"), data.Value<bool>("loop"), finished, 0f);
                loadedClip = null;
                AccessTools.Property(typeof(AudioMgrEx), "musicUrl").SetValue(null, url, null);
                channel.source.time = Mathf.Clamp(data.Value<float>("seconds"), 0, Mathf.Max(0, channel.source.clip.length - .01f));
            }
            finally
            {
                abandoned = true;
                if (loadedClip != null) ResMgr.Recycle(loadedClip);
            }
        }

        private static void Silent(Channel channel, string reason)
        {
            if (channel != null && channel.source != null) { channel.callback = null; channel.Stop(null, 0f); }
            AccessTools.Property(typeof(AudioMgrEx), "musicUrl").SetValue(null, null, null);
            Debug.LogWarning("[DialogueSave] " + reason);
        }

        private static bool KnownBgmCallback(Action callback, int depth)
        {
            if (callback == null) return true;
            if (depth > 5 || callback.GetInvocationList().Length != 1) return false;
            if (callback.Target is BgmContinuation)
                return callback.Method.DeclaringType == typeof(BgmContinuation) && callback.Method.Name == "Invoke";
            if (callback.Target == null && callback.Method.DeclaringType == typeof(AudioMgrEx))
                return callback.Method.Name == "PlayBgm" && callback.Method.GetParameters().Length == 0;
            Type owner = callback.Method.DeclaringType;
            if (owner?.DeclaringType != typeof(AudioMgrEx) || callback.Target == null ||
                !callback.Method.Name.StartsWith("<PlayGroupBgm>", StringComparison.Ordinal)) return false;
            // The native playlist lambda may carry an external completion delegate.
            // Allow only its music-only continuation, never a gameplay callback.
            foreach (FieldInfo field in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (field.FieldType == typeof(Action) && !KnownBgmCallback((Action)field.GetValue(callback.Target), depth + 1)) return false;
            return true;
        }

        private static bool KnownUrl(string url) => !string.IsNullOrEmpty(url) && Cfg.AudioCfgMap.Values.Any(cfg => cfg.url == url);
        private static T Field<T>(object obj, string name) => (T)(AccessTools.Field(obj.GetType(), name) ?? throw new MissingFieldException(obj.GetType().Name, name)).GetValue(obj);

        private static bool KnownCallback(Action callback, int depth)
        {
            if (callback == null) return true;
            if (depth > 5) return false;
            foreach (Delegate item in callback.GetInvocationList())
            {
                if (item.Target is BgmContinuation) continue;
                Type type = item.Method.DeclaringType;
                if (type == typeof(AudioMgrEx)) continue;
                if (type?.DeclaringType != typeof(AudioMgrEx)) return false;
                // A native closure can itself wrap an external callback; inspect nested Actions as well.
                if (item.Target != null)
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (field.FieldType == typeof(Action) && !KnownCallback((Action)field.GetValue(item.Target), depth + 1)) return false;
            }
            return true;
        }

        private sealed class BgmContinuation
        {
            internal readonly string Kind;
            internal readonly int Group;
            internal readonly bool Random;
            internal BgmContinuation(string kind, int group, bool random) { Kind = kind; Group = group; Random = random; }
            internal void Invoke()
            {
                if (Kind == "group") AudioMgrEx.PlayGroupBgm((AudioGroupDefine)Group, Random);
                else if (Kind == "default") AudioMgrEx.PlayBgm();
            }
        }
    }
}
