using GenUI.Main;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    internal static class DialogueKeycap
    {
        private static TMP_FontAsset[] latinFonts;
        private static float nextFontLookup;
        private static Font fallbackFont;

        private static TMP_FontAsset LatinFont(string label)
        {
            // Four buttons and native toolbar refreshes share the same loaded faces.
            // Retry a missing/destroyed asset later, without four global scans per row.
            if (latinFonts == null || (Time.realtimeSinceStartup >= nextFontLookup &&
                (latinFonts.Length == 0 || latinFonts.Any(f => f == null))))
            {
                nextFontLookup = Time.realtimeSinceStartup + 5f;
                latinFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                    .Where(f => f != null && (f.name == "LiberationSans SDF" || f.name == "SourceHanSansCN-Regular SDF"))
                    .OrderBy(f => f.name == "LiberationSans SDF" ? 0 : 1).ToArray();
            }
            return latinFonts.FirstOrDefault(f => f != null && label.All(c => f.HasCharacter(c)));
        }

        internal static string ActionLabel(DialogueHotkeyAction action)
        {
            switch (action)
            {
                case DialogueHotkeyAction.Save: return "保存";
                case DialogueHotkeyAction.Load: return "加载";
                case DialogueHotkeyAction.QuickSave: return "快速保存";
                default: return "快速加载";
            }
        }

        internal static void Apply(Cell_KeyItemUI cell, DialogueHotkeyBinding binding)
        {
            if (binding == null || string.IsNullOrEmpty(binding.AtlasUrl)) return;
            cell.icon_item.SetAtlasUrl(binding.AtlasUrl);
            var capImage = cell.icon_item.gameObject.GetComponent<Image>();
            if (binding.RenderLabelOnKeycap && capImage != null)
            {
                capImage.preserveAspect = false;
                capImage.type = Image.Type.Sliced;
            }
            var previous = cell.icon_item.transform.Find("DialogueSave.KeyLabel");
            if (previous != null) NativeUiCloner.Remove(previous.gameObject);
            if (!binding.RenderLabelOnKeycap) return;
            // Native key labels are baked into their sprites. Do not inherit the
            // Chinese caption's heavy serif face for the missing Latin labels.
            // Native common6 F/S/Tab/Esc glyphs are warm brown and bold; their
            // cap height is 23–24 pixels in a 64-pixel keycap, not a full-height label.
            var font = LatinFont(binding.Label);
            var obj = new GameObject("DialogueSave.KeyLabel", typeof(RectTransform));
            obj.layer = cell.icon_item.gameObject.layer;
            obj.transform.SetParent(cell.icon_item.transform, false);
            var rect = (RectTransform)obj.transform;
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
            float capHeight = cell.icon_item.transform.rect.height * (23.5f / 64f);
            float capRatio = font != null && font.faceInfo.pointSize > 0 && font.faceInfo.capLine > 0
                ? font.faceInfo.capLine / font.faceInfo.pointSize : .69f;
            float size = capHeight / capRatio;
            Color nativeInk = new Color32(115, 77, 49, 255);
            if (font != null)
            {
                var letter = obj.AddComponent<TextMeshProUGUI>();
                letter.font = font; letter.fontSharedMaterial = font.material;
                letter.text = binding.Label; letter.richText = false; letter.raycastTarget = false;
                letter.alignment = TextAlignmentOptions.Center;
                letter.color = nativeInk; letter.fontStyle = FontStyles.Bold;
                letter.fontWeight = FontWeight.Bold;
                letter.enableVertexGradient = false; letter.enableAutoSizing = false; letter.fontSize = size;
            }
            else
            {
                var letter = obj.AddComponent<Text>();
                if (fallbackFont == null) fallbackFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                letter.font = fallbackFont;
                letter.text = binding.Label; letter.supportRichText = false; letter.raycastTarget = false;
                letter.alignment = TextAnchor.MiddleCenter; letter.color = nativeInk;
                letter.fontStyle = FontStyle.Bold; letter.fontSize = Mathf.RoundToInt(size);
            }
            obj.SetActive(true);
        }
    }
}
