using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    // Captures existing layout only: no files, re-layout, input, or scene mutation.
    internal static class UiLayoutDiagnostics
    {
        internal static JObject Rect(RectTransform rect)
        {
            if (rect == null) return new JObject { ["missing"] = true };
            var bounds = ScreenBounds(rect);
            return new JObject
            {
                ["name"] = rect.name,
                ["activeSelf"] = rect.gameObject.activeSelf,
                ["activeInHierarchy"] = rect.gameObject.activeInHierarchy,
                ["width"] = rect.rect.width,
                ["height"] = rect.rect.height,
                ["anchorMin"] = Vector(rect.anchorMin),
                ["anchorMax"] = Vector(rect.anchorMax),
                ["pivot"] = Vector(rect.pivot),
                ["anchoredPosition"] = Vector(rect.anchoredPosition),
                ["localScale"] = new JArray(rect.localScale.x, rect.localScale.y, rect.localScale.z),
                ["screenBounds"] = new JObject { ["x"] = bounds.x, ["y"] = bounds.y, ["width"] = bounds.width, ["height"] = bounds.height },
                ["insideScreen"] = bounds.xMin >= -1 && bounds.yMin >= -1 && bounds.xMax <= Screen.width + 1 && bounds.yMax <= Screen.height + 1
            };
        }

        internal static JObject Text(Text text)
        {
            return new JObject
            {
                ["text"] = text.text,
                ["font"] = text.font == null ? null : text.font.name,
                ["fontSize"] = text.fontSize,
                ["alignment"] = text.alignment.ToString(),
                ["color"] = Color(text.color),
                ["rect"] = Rect(text.rectTransform)
            };
        }

        internal static JArray Graphics(Transform root)
        {
            var result = new JArray();
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
            {
                var image = graphic as Image;
                var text = graphic as Text;
                var tmp = graphic as TMP_Text;
                result.Add(new JObject
                {
                    ["name"] = graphic.name,
                    ["type"] = graphic.GetType().Name,
                    ["enabled"] = graphic.enabled,
                    ["active"] = graphic.gameObject.activeInHierarchy,
                    ["color"] = Color(graphic.color),
                    ["rendererAlpha"] = graphic.canvasRenderer.GetAlpha(),
                    ["culled"] = graphic.canvasRenderer.cull,
                    ["sprite"] = image == null || image.sprite == null ? null : image.sprite.name,
                    ["font"] = text != null && text.font != null ? text.font.name : tmp != null && tmp.font != null ? tmp.font.name : null,
                    ["text"] = text != null ? text.text : tmp != null ? tmp.text : null,
                    ["rect"] = Rect(graphic.rectTransform)
                });
            }
            return result;
        }

        internal static JObject Gradient(VertexGradient gradient)
        {
            return new JObject
            {
                ["topLeft"] = Color(gradient.topLeft), ["topRight"] = Color(gradient.topRight),
                ["bottomLeft"] = Color(gradient.bottomLeft), ["bottomRight"] = Color(gradient.bottomRight)
            };
        }

        internal static bool SameColor(UnityEngine.Color a, UnityEngine.Color b)
        {
            return Mathf.Abs(a.r - b.r) < .001f && Mathf.Abs(a.g - b.g) < .001f &&
                Mathf.Abs(a.b - b.b) < .001f && Mathf.Abs(a.a - b.a) < .001f;
        }

        internal static bool SameGradient(VertexGradient a, VertexGradient b)
        {
            return SameColor(a.topLeft, b.topLeft) && SameColor(a.topRight, b.topRight) &&
                SameColor(a.bottomLeft, b.bottomLeft) && SameColor(a.bottomRight, b.bottomRight);
        }

        internal static bool SameFont(Text a, Text b)
        {
            return a.font == b.font && a.fontSize == b.fontSize && a.fontStyle == b.fontStyle;
        }

        internal static bool SameSize(RectTransform a, RectTransform b)
        {
            return Mathf.Abs(a.rect.width - b.rect.width) < .05f && Mathf.Abs(a.rect.height - b.rect.height) < .05f;
        }

        internal static bool Contains(RectTransform parent, RectTransform child)
        {
            var outer = ScreenBounds(parent); var inner = ScreenBounds(child);
            return inner.xMin >= outer.xMin - 1 && inner.yMin >= outer.yMin - 1 &&
                inner.xMax <= outer.xMax + 1 && inner.yMax <= outer.yMax + 1;
        }

        internal static bool DoNotOverlap(IEnumerable<RectTransform> transforms)
        {
            var bounds = transforms.Select(ScreenBounds).ToArray();
            for (int i = 0; i < bounds.Length; i++) for (int j = i + 1; j < bounds.Length; j++)
                if (Mathf.Min(bounds[i].xMax, bounds[j].xMax) - Mathf.Max(bounds[i].xMin, bounds[j].xMin) > 1 &&
                    Mathf.Min(bounds[i].yMax, bounds[j].yMax) - Mathf.Max(bounds[i].yMin, bounds[j].yMin) > 1)
                    return false;
            return true;
        }

        private static UnityEngine.Rect ScreenBounds(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var points = corners.Select(x => RectTransformUtility.WorldToScreenPoint(camera, x)).ToArray();
            return UnityEngine.Rect.MinMaxRect(points.Min(x => x.x), points.Min(x => x.y), points.Max(x => x.x), points.Max(x => x.y));
        }

        private static JArray Color(UnityEngine.Color value) { return new JArray(value.r, value.g, value.b, value.a); }
        private static JArray Vector(Vector2 value) { return new JArray(value.x, value.y); }
    }
}
