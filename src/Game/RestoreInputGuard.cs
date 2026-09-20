using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;

namespace StudentAgeDialogueSave.GameIntegration
{
    /// <summary>Own overlay survives CloseAllView and restores the exact prior native action-map enable state.</summary>
    internal sealed class RestoreInputGuard : IDisposable
    {
        private readonly GameObject overlay;
        private readonly GameInput controls;
        private readonly bool uiEnabled;
        private readonly bool playerEnabled;
        private bool disposed;

        internal RestoreInputGuard(NewTalkView dialogue)
        {
            var controller = (Control)AccessTools.Field(typeof(Control), "ins").GetValue(null);
            if (controller == null) throw new InvalidOperationException("输入系统尚未准备好");
            controls = (GameInput)AccessTools.Field(typeof(Control), "controls").GetValue(controller);
            if (controls == null) throw new InvalidOperationException("输入映射尚未准备好");
            uiEnabled = controls.UI.enabled;
            playerEnabled = controls.Player.enabled;
            overlay = new GameObject("DialogueSaveRestoreBlocker", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            try
            {
                overlay.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(overlay);
                var canvas = overlay.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = short.MaxValue;
                var shade = new GameObject("InputBlocker", typeof(RectTransform), typeof(Image));
                shade.transform.SetParent(overlay.transform, false);
                var rect = shade.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var image = shade.GetComponent<Image>();
                image.color = Color.clear;
                image.raycastTarget = true;
                controls.UI.Disable(); controls.Player.Disable();
            }
            catch
            {
                UnityEngine.Object.Destroy(overlay);
                throw;
            }
        }

        internal void Release(bool restoreInput)
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (restoreInput)
                {
                    if (uiEnabled) controls.UI.Enable(); else controls.UI.Disable();
                    if (playerEnabled) controls.Player.Enable(); else controls.Player.Disable();
                }
            }
            finally
            {
                // InputSystem may already be torn down. A failure there must never strand
                // the top-level raycast blocker over the game or title screen.
                if (overlay != null) UnityEngine.Object.Destroy(overlay);
            }
        }

        public void Dispose() => Release(true);
    }
}
