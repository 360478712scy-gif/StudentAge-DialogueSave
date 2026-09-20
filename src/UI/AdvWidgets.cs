using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    // Vertex colours give the dialogue a genuinely transparent upper edge. No
    // full-screen blur, render texture, shader animation or per-frame mesh rebuild.
    internal sealed class AdvVeil : MaskableGraphic
    {
        internal void SetDark(bool dark){color=dark?new Color(.075f,.072f,.068f,.72f):new Color(.98f,.973f,.949f,.87f);}
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); var r=rectTransform.rect;
            float[] stops={0,.24f,.58f,.82f,1};
            float[] alpha={.99f,.97f,.9f,.48f,0};
            for(int i=0;i<stops.Length;i++)
            {
                float y=r.yMin+r.height*stops[i];
                var c=color;c.a*=alpha[i];
                vh.AddVert(new Vector3(r.xMin,y),c,Vector2.zero);
                vh.AddVert(new Vector3(r.xMax,y),c,Vector2.one);
                if(i>0){int n=i*2;vh.AddTriangle(n-2,n-1,n);vh.AddTriangle(n-1,n+1,n);}
            }
        }
    }

    internal sealed class AdvPaper : MaskableGraphic
    {
        public float Cut=12;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); var r=rectTransform.rect;float c=Mathf.Min(Cut,Mathf.Min(r.width,r.height)*.25f);
            Vector2[] p={new Vector2(r.xMin+c,r.yMin),new Vector2(r.xMax-c,r.yMin),new Vector2(r.xMax,r.yMin+c),
                new Vector2(r.xMax,r.yMax-c),new Vector2(r.xMax-c,r.yMax),new Vector2(r.xMin+c,r.yMax),
                new Vector2(r.xMin,r.yMax-c),new Vector2(r.xMin,r.yMin+c)};
            vh.AddVert(r.center,color,Vector2.zero);
            foreach(var v in p)vh.AddVert(v,color,Vector2.zero);
            for(int i=0;i<8;i++)vh.AddTriangle(0,i+1,(i+1)%8+1);
        }
    }

    internal sealed class AdvChoiceVeil : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();var r=rectTransform.rect;
            float[] stops={0,.055f,.16f,.84f,.945f,1};float[] a={0,.38f,.97f,.97f,.38f,0};
            for(int i=0;i<stops.Length;i++)
            {
                float x=r.xMin+stops[i]*r.width;var c=color;c.a*=a[i];
                vh.AddVert(new Vector3(x,r.yMin),c,Vector2.zero);vh.AddVert(new Vector3(x,r.yMax),c,Vector2.one);
                if(i>0){int n=i*2;vh.AddTriangle(n-2,n,n-1);vh.AddTriangle(n-1,n,n+1);}
            }
        }
    }

    internal sealed class AdvButton : Button
    {
        public override void OnSubmit(UnityEngine.EventSystems.BaseEventData eventData)
        {
            if(AdvDialogueController.Active?.HandleSpace()==true){eventData.Use();return;}
            base.OnSubmit(eventData);
        }
    }

    internal static class AdvWidgets
    {
        internal static readonly Color Ink=new Color32(41,38,35,255), Blue=new Color32(142,119,81,255),
            Gold=new Color32(189,161,114,255), Paper=new Color32(250,248,242,255), Muted=new Color32(103,91,73,255),
            DarkPaper=new Color(.075f,.072f,.068f,.93f);
        static readonly Dictionary<TMP_FontAsset,Material> outlined=new Dictionary<TMP_FontAsset,Material>();
        static TMP_FontAsset readingFont;
        internal static TMP_FontAsset ReadingFont(TMP_FontAsset fallback,bool refresh=false)
        {
            if(readingFont!=null && !refresh)return readingFont;
            var fonts=Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            readingFont=fonts.FirstOrDefault(f=>f.name=="SourceHanSansCN-Medium SDF")??
                fonts.FirstOrDefault(f=>f.faceInfo.familyName=="Source Han Sans CN");
            return readingFont??fallback;
        }
        internal static Material Outline(TMP_FontAsset font)
        {
            if(outlined.TryGetValue(font,out var material) && material!=null)return material;
            material=new Material(font.material){name="ADV.ReadingInk",hideFlags=HideFlags.HideAndDontSave};
            if(material.HasProperty("_FaceColor"))material.SetColor("_FaceColor",Color.white);
            if(material.HasProperty("_OutlineColor"))material.SetColor("_OutlineColor",new Color(.08f,.07f,.06f,1));
            if(material.HasProperty("_OutlineWidth"))material.SetFloat("_OutlineWidth",0);
            if(material.HasProperty("_OutlineSoftness"))material.SetFloat("_OutlineSoftness",0);
            material.DisableKeyword("OUTLINE_ON");outlined[font]=material;return material;
        }
        internal static void ReleaseMaterials(){foreach(var m in outlined.Values)if(m!=null)UnityEngine.Object.Destroy(m);outlined.Clear();readingFont=null;}
        internal static void ThemeButton(Button b,bool filled,bool dark)
        {
            var cs=b.colors;cs.normalColor=filled?(dark?DarkPaper:Paper):new Color(1,1,1,.02f);
            cs.highlightedColor=dark?new Color(.27f,.23f,.16f,.95f):new Color(.94f,.91f,.84f,1);
            cs.pressedColor=dark?new Color(.37f,.29f,.17f,1):new Color(.86f,.82f,.73f,1);
            cs.selectedColor=cs.highlightedColor;cs.fadeDuration=.08f;b.colors=cs;
        }
        internal static RectTransform Rect(string name,Transform parent,float x,float y,float w,float h)
        {
            var go=new GameObject(name,typeof(RectTransform));var r=go.GetComponent<RectTransform>();
            r.SetParent(parent,false);r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=new Vector2(0,1);
            r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);return r;
        }
        internal static void Fill(RectTransform r)
        {r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=r.offsetMax=Vector2.zero;}
        internal static Image Box(string name,Transform parent,float x,float y,float w,float h,Color color,bool hit=false)
        {var r=Rect(name,parent,x,y,w,h);var img=r.gameObject.AddComponent<Image>();img.color=color;img.raycastTarget=hit;return img;}
        internal static AdvPaper Card(string name,Transform parent,float x,float y,float w,float h,Color color)
        {var r=Rect(name,parent,x,y,w,h);var g=r.gameObject.AddComponent<AdvPaper>();g.color=color;g.raycastTarget=false;return g;}
        internal static TextMeshProUGUI Label(string name,Transform parent,TMP_FontAsset font,string text,float x,float y,float w,float h,float size,Color color)
        {
            var r=Rect(name,parent,x,y,w,h);var t=r.gameObject.AddComponent<TextMeshProUGUI>();
            t.font=ReadingFont(font);t.fontSharedMaterial=Outline(t.font);t.fontSize=size;t.text=text;t.color=color;t.raycastTarget=false;t.richText=false;
            t.alignment=TextAlignmentOptions.MidlineLeft;t.enableWordWrapping=false;return t;
        }
        internal static Button Button(string name,Transform parent,TMP_FontAsset font,string caption,float x,float y,float w,float h,Action click,bool filled=false,bool fadeEdges=false)
        {
            var r=Rect(name,parent,x,y,w,h);
            MaskableGraphic bg;
            if(fadeEdges)bg=r.gameObject.AddComponent<AdvChoiceVeil>();
            else{var paper=r.gameObject.AddComponent<AdvPaper>();paper.Cut=7;bg=paper;}
            bg.color=Color.white;bg.raycastTarget=true;
            var b=r.gameObject.AddComponent<AdvButton>();b.targetGraphic=bg;b.transition=Selectable.Transition.ColorTint;
            ThemeButton(b,filled,false);
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;
            var t=Label("Caption",r,font,caption,4,0,w-8,h,20,Ink);t.alignment=TextAlignmentOptions.Center;
            b.onClick.AddListener(()=>click());return b;
        }
        internal static GameObject Canvas(string name,int order)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(go);go.hideFlags=HideFlags.HideAndDontSave;
            var canvas=go.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=order;
            var scaler=go.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
            return go;
        }
    }
}
