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
    internal sealed class AdvVeil : RawImage
    {
        internal void SetReadingTone(){texture=AdvSkin.Texture("window.png");color=new Color(.28f,.3f,.48f,1);}
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
        bool held;
        protected override void DoStateTransition(SelectionState state,bool instant)
        {base.DoStateTransition(held && IsInteractable()?SelectionState.Pressed:state,instant);}
        public override void OnPointerDown(UnityEngine.EventSystems.PointerEventData e)
        {if(e.button==UnityEngine.EventSystems.PointerEventData.InputButton.Left && IsInteractable())held=true;base.OnPointerDown(e);}
        public override void OnPointerUp(UnityEngine.EventSystems.PointerEventData e)
        {if(e.button==UnityEngine.EventSystems.PointerEventData.InputButton.Left)held=false;base.OnPointerUp(e);}
        protected override void OnDisable(){held=false;base.OnDisable();}
        protected override void Awake()
        {base.Awake();onClick.AddListener(()=>{if(IsInteractable())AdvSkin.PlayClick(false);});}
        public override void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
        {base.OnPointerEnter(eventData);if(!held && IsActive() && IsInteractable())AdvSkin.PlayHover();}

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
        static readonly Dictionary<TMP_FontAsset,Material> lightOutlined=new Dictionary<TMP_FontAsset,Material>();
        static readonly Dictionary<TMP_FontAsset,Material> dialogueOutlined=new Dictionary<TMP_FontAsset,Material>();
        static readonly Dictionary<TMP_FontAsset,Material> tabOutlined=new Dictionary<TMP_FontAsset,Material>();
        static TMP_FontAsset readingFont;
        // Local preview may supply a process-private font; no system font file is shipped.
        internal static TMP_FontAsset DialogueFontOverride;
        internal static TMP_FontAsset ReadingFont(TMP_FontAsset fallback,bool refresh=false)
        {
            var chinese=AdvChineseFont.Get(fallback);
            if(chinese!=null)return chinese;
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
        internal static Material LightOutline(TMP_FontAsset font)
        {
            if(lightOutlined.TryGetValue(font,out var m) && m!=null)return m;
            m=new Material(Outline(font)){name="ADV.LightReadingInk"};
            if(m.HasProperty("_OutlineWidth"))m.SetFloat("_OutlineWidth",.085f);
            if(m.HasProperty("_OutlineColor"))m.SetColor("_OutlineColor",new Color(.12f,.12f,.14f,.8f));
            m.EnableKeyword("OUTLINE_ON");lightOutlined[font]=m;return m;
        }
        internal static Material DialogueShadow(TMP_FontAsset font)
        {
            if(dialogueOutlined.TryGetValue(font,out var m) && m!=null)return m;
            m=new Material(LightOutline(font)){name="ADV.DialogueShadow"};
            m.SetFloat("_OutlineWidth",.12f);m.SetColor("_OutlineColor",new Color(0,0,0,.95f));
            m.SetColor("_UnderlayColor",new Color(0,0,0,.85f));
            m.SetFloat("_UnderlayOffsetX",.55f);m.SetFloat("_UnderlayOffsetY",-.65f);
            m.SetFloat("_UnderlayDilate",.08f);m.SetFloat("_UnderlaySoftness",.12f);m.EnableKeyword("UNDERLAY_ON");
            dialogueOutlined[font]=m;return m;
        }
        internal static Material TabOutline(TMP_FontAsset font)
        {
            if(tabOutlined.TryGetValue(font,out var material) && material!=null)return material;
            material=new Material(Outline(font)){name="ADV.SettingsTabInk",hideFlags=HideFlags.HideAndDontSave};
            material.SetColor("_FaceColor",new Color32(0,34,68,255));
            material.SetColor("_OutlineColor",new Color32(255,249,239,255));
            material.SetFloat("_OutlineWidth",.35f);material.SetFloat("_OutlineSoftness",.01f);
            material.EnableKeyword("OUTLINE_ON");tabOutlined[font]=material;return material;
        }
        internal static void ReleaseMaterials(){foreach(var m in outlined.Values)if(m!=null)UnityEngine.Object.Destroy(m);outlined.Clear();foreach(var m in lightOutlined.Values)if(m!=null)UnityEngine.Object.Destroy(m);lightOutlined.Clear();foreach(var m in tabOutlined.Values)if(m!=null)UnityEngine.Object.Destroy(m);tabOutlined.Clear();foreach(var m in dialogueOutlined.Values)if(m!=null)UnityEngine.Object.Destroy(m);dialogueOutlined.Clear();readingFont=null;
            AdvChineseFont.Release();
        }
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
        // Pages are authored top-left in 1920x1080. On other window shapes the Expand scaler
        // adds room below or to the right; center the design area like the native letterbox, unscaled.
        internal static void CenterDesign(GameObject canvas){if(canvas.GetComponent<DesignStage>()==null)canvas.AddComponent<DesignStage>();}
        internal static GameObject Canvas(string name,int order)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(go);go.hideFlags=HideFlags.HideAndDontSave;
            var canvas=go.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=order;
            var scaler=go.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
            if(name!="DialogueSave.ADV.OptionTooltip")Letterbox.Track(go);
            return go;
        }
    }
    internal sealed class DesignStage:MonoBehaviour
    {
        readonly Dictionary<RectTransform,Vector2> positions=new Dictionary<RectTransform,Vector2>();
        // Runs just before canvases render, so children created this frame (tab rebuilds,
        // transition slats) are never drawn at their uncentered design position.
        void OnEnable(){Canvas.willRenderCanvases+=Apply;Apply();}
        void OnDisable(){Canvas.willRenderCanvases-=Apply;}
        // Expand scaler: canvas = screen / min(screen/reference), known before the scaler runs.
        internal static Vector2 CanvasSize(){float scale=Mathf.Min(Screen.width/1920f,Screen.height/1080f);return scale<=0?new Vector2(1920,1080):new Vector2(Screen.width/scale,Screen.height/scale);}
        internal void Apply()
        {
            if(this==null)return;
            foreach(var gone in positions.Keys.Where(k=>k==null).ToArray())positions.Remove(gone);
            var size=CanvasSize();var offset=new Vector2((size.x-1920f)/2f,-(size.y-1080f)/2f);
            var top=new Vector2(0,1);
            foreach(Transform child in transform)
            {
                var r=child as RectTransform;
                if(r==null || r.anchorMin!=top || r.anchorMax!=top || r.pivot!=top)continue;
                if(!positions.TryGetValue(r,out var design))positions[r]=design=r.anchoredPosition;
                var target=design+offset;
                if(r.anchoredPosition!=target)r.anchoredPosition=target;
            }
        }
    }
    // The game paints nothing outside its centered 16:9 picture. While any mod view is shown,
    // cover those bars in black above every mod canvas, so art that runs past the picture
    // edge (and any sub-pixel seam at that edge) never shows, without clipping mod layout.
    internal sealed class Letterbox:MonoBehaviour
    {
        static Letterbox instance;
        static readonly List<GameObject> tracked=new List<GameObject>();
        RectTransform first,second;
        internal static void Track(GameObject canvas)
        {
            tracked.Add(canvas);
            if(instance!=null)return;
            var go=new GameObject("DialogueSave.Letterbox",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler));
            UnityEngine.Object.DontDestroyOnLoad(go);go.hideFlags=HideFlags.HideAndDontSave;
            var c=go.GetComponent<Canvas>();c.renderMode=RenderMode.ScreenSpaceOverlay;c.sortingOrder=32760;
            var scaler=go.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
            instance=go.AddComponent<Letterbox>();instance.first=instance.Bar("Bar A");instance.second=instance.Bar("Bar B");instance.Refresh();
        }
        internal static void Release(){tracked.Clear();if(instance!=null)UnityEngine.Object.Destroy(instance.gameObject);instance=null;}
        RectTransform Bar(string name)
        {
            var image=new GameObject(name,typeof(RectTransform),typeof(Image)).GetComponent<Image>();
            image.rectTransform.SetParent(transform,false);image.color=Color.black;image.raycastTarget=false;return image.rectTransform;
        }
        void OnEnable(){Canvas.willRenderCanvases+=Refresh;}
        void OnDisable(){Canvas.willRenderCanvases-=Refresh;}
        void Refresh()
        {
            if(this==null || first==null)return;
            tracked.RemoveAll(g=>g==null);
            // Only screens clearly taller than 16:9, where the game itself shows black bands.
            // 16:9, near-16:9 (e.g. 1366x768) and wider screens never get bars from the mod.
            var size=DesignStage.CanvasSize();float tall=(size.y-1080f)/2f;
            bool show=tall>=1080f*.01f && tracked.Any(g=>g.activeInHierarchy);
            if(first.gameObject.activeSelf!=show){first.gameObject.SetActive(show);second.gameObject.SetActive(show);}
            if(!show)return;
            Place(first,new Vector2(0,1),new Vector2(1,1),new Vector2(0,tall));Place(second,new Vector2(0,0),new Vector2(1,0),new Vector2(0,tall));
        }
        static void Place(RectTransform r,Vector2 min,Vector2 max,Vector2 size)
        {
            r.anchorMin=min;r.anchorMax=max;r.pivot=new Vector2(min.x==max.x?min.x:.5f,min.y==max.y?min.y:.5f);
            r.anchoredPosition=Vector2.zero;r.sizeDelta=size;
        }
    }
}
