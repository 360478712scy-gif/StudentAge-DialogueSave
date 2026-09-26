using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace StudentAgeDialogueSave.UI
{
    // Embedded, decoded once per plugin lifetime. No image loading in the typing path.
    internal static class AdvSkin
    {
        static readonly Dictionary<string,Texture2D> textures=new Dictionary<string,Texture2D>();
        static JObject layout;
        static AudioClip clickSound,toggleSound,hoverSound;
        static AudioSource uiAudio;
        internal static int PlayedHovers {get;private set;}
        internal static int PlayedClicks {get;private set;}
        internal static float ButtonVolume=.8f;
        internal static Rect LogoUv {get;private set;}=new Rect(0,0,1,1);
        internal static JObject Layout
        {
            get {if(layout==null)using(var s=Open("layout.json"))using(var r=new StreamReader(s))layout=JObject.Parse(r.ReadToEnd());return layout;}
        }
        static Stream Open(string name)=>typeof(AdvSkin).Assembly.GetManifestResourceStream("DialogueSave.Skin."+name)??throw new FileNotFoundException(name);
        internal static void PrepareAudio()
        {
            if(clickSound!=null)return;
            clickSound=ReadWave("click.wav");toggleSound=ReadWave("toggle.wav");hoverSound=ReadWave("hover.wav");
        }
        static AudioClip ReadWave(string name)
        {
            using(var r=new BinaryReader(Open(name)))
            {
                if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException(name);
                r.ReadInt32();r.ReadChars(4);int channels=0,rate=0;byte[] data=null;
                while(r.BaseStream.Position+8<=r.BaseStream.Length)
                {
                    string kind=new string(r.ReadChars(4));int length=r.ReadInt32();long next=r.BaseStream.Position+length+(length&1);
                    if(kind=="fmt "){if(r.ReadInt16()!=1)throw new InvalidDataException(name);channels=r.ReadInt16();rate=r.ReadInt32();r.ReadInt32();r.ReadInt16();if(r.ReadInt16()!=16)throw new InvalidDataException(name);}
                    else if(kind=="data")data=r.ReadBytes(length);
                    r.BaseStream.Position=next;
                }
                if(data==null || channels==0 || rate==0)throw new InvalidDataException(name);
                var samples=new float[data.Length/2];for(int i=0;i<samples.Length;i++)samples[i]=(short)(data[i*2]|data[i*2+1]<<8)/32768f;
                var clip=AudioClip.Create("ADV."+name,samples.Length/channels,channels,rate,false);clip.SetData(samples,0);return clip;
            }
        }
        internal static void PlayClick(bool toggle)
        {
            PrepareAudio();
            // Native UI channel retains the game's sound mixer volume/mute settings.
            if(uiAudio==null && Sdk.AudioMgr.Ins!=null)
            {var channels=Sdk.AudioMgr.Ins.GetComponents<AudioSource>();if(channels.Length>0)uiAudio=channels[0];}
            if(uiAudio!=null){uiAudio.PlayOneShot(toggle?toggleSound:clickSound,ButtonVolume);PlayedClicks++;}
        }
        internal static void PlayHover()
        {
            PrepareAudio();
            if(uiAudio==null && Sdk.AudioMgr.Ins!=null){var channels=Sdk.AudioMgr.Ins.GetComponents<AudioSource>();if(channels.Length>0)uiAudio=channels[0];}
            if(uiAudio!=null){uiAudio.PlayOneShot(hoverSound,ButtonVolume);PlayedHovers++;}
        }
        internal static Texture2D Texture(string name)
        {
            if(textures.TryGetValue(name,out var cached))return cached;
            byte[] bytes;using(var s=Open(name=="choice-disabled.png"?"choice-idle.png":name))using(var m=new MemoryStream()){s.CopyTo(m);bytes=m.ToArray();}
            var t=new Texture2D(2,2,TextureFormat.RGBA32,false){name="ADV.Skin."+name,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            if(!ImageConversion.LoadImage(t,bytes))throw new InvalidDataException(name);
            if(name.StartsWith("confirm-"))
            {
                var pixels=t.GetPixels32();
                for(int i=0;i<pixels.Length;i++){var p=pixels[i];if(p.r>p.b*1.4f && p.g>p.b*1.3f && p.r>100){if(name=="confirm-frame__null_.png")pixels[i]=new Color32(p.r,p.g,p.b,0);else pixels[i]=new Color32(45,160,225,p.a);}}
                t.SetPixels32(pixels);t.Apply(false,true);
            }
            else if(name=="backlog-frame.png" || name=="backlog-date.png")
            {
                var pixels=t.GetPixels32();bool alpha=false;foreach(var p in pixels)if(p.a<250){alpha=true;break;}
                if(!alpha)for(int i=0;i<pixels.Length;i++)
                {
                    var p=pixels[i];float r=p.r/255f,g=p.g/255f,b=p.b/255f;
                    float a=Mathf.Clamp01(1-Mathf.Min(r,b)+g);
                    pixels[i]=a<=.25f?Color.clear:new Color(Mathf.Clamp01((r-1+a)/a),Mathf.Clamp01(g/a),Mathf.Clamp01((b-1+a)/a),(a-.25f)/.75f);
                }
                t.SetPixels32(pixels);t.Apply(false,false);
            }
            else if(name=="choice-stationery-mask.png")
            {
                var pixels=t.GetPixels32();
                for(int i=0;i<pixels.Length;i++){byte a=pixels[i].r;pixels[i]=new Color32(255,255,255,a);}
                t.SetPixels32(pixels);t.Apply(false,true);
            }
            else if(name.StartsWith("choice-"))
            {
                var pixels=t.GetPixels32();bool disabled=name=="choice-disabled.png";
                for(int i=0;i<pixels.Length;i++)
                {
                    var p=pixels[i];
                    if(disabled){byte gray=(byte)(p.r*.2126f+p.g*.7152f+p.b*.0722f);pixels[i]=new Color32(gray,gray,gray,p.a);}
                    else if(p.r>p.b*1.4f && p.r>p.g*1.08f)
                    {float strength=p.r/255f;pixels[i]=new Color32((byte)(65*strength),(byte)(180*strength),(byte)(255*strength),p.a);}
                }
                t.SetPixels32(pixels);t.Apply(false,true);
            }
            else if(name=="logo-mask.png")
            {
                // Generated white-on-black artwork is a coverage mask. Both the
                // background and English cut-throughs become real transparency.
                var pixels=t.GetPixels32();int left=t.width,bottom=t.height,right=0,top=0;
                for(int i=0;i<pixels.Length;i++)
                {
                    var p=pixels[i];byte a=(byte)Mathf.Clamp((Mathf.Max(p.r,Mathf.Max(p.g,p.b))-12)*255f/243f,0,255);
                    pixels[i]=new Color32(255,255,255,a);
                    if(a>32){int x=i%t.width,y=i/t.width;left=Math.Min(left,x);right=Math.Max(right,x);bottom=Math.Min(bottom,y);top=Math.Max(top,y);}
                }
                t.SetPixels32(pixels);t.Apply(false,true);
                LogoUv=new Rect((float)left/t.width,(float)bottom/t.height,(float)(right-left+1)/t.width,(float)(top-bottom+1)/t.height);
            }
            else
            {
                if(name!="hold_normal_on.png" && (name.Contains("_over") || name.EndsWith("_on.png") || name.Contains("_on_")))
                {
                    // Blue hover/pressed/latched states, retaining source alpha and glow.
                    bool hover=name.Contains("_over");float red=hover?.42f:.25f,green=hover?.82f:.67f;
                    var pixels=t.GetPixels32();
                    for(int i=0;i<pixels.Length;i++){var p=pixels[i];byte light=Math.Max(p.r,Math.Max(p.g,p.b));pixels[i]=new Color32((byte)(light*red),(byte)(light*green),light,p.a);}
                    t.SetPixels32(pixels);
                }
                t.Apply(false,true);
            }
            textures.Add(name,t);return t;
        }
        internal static Texture2D NativeAlpha(Texture source)
        {
            string key="native-alpha-"+source.GetInstanceID();
            if(textures.TryGetValue(key,out var cached))return cached;
            // The CG mask stores black RGB in the bitmap. Keep every alpha value
            // and its native geometry; make RGB white so the usual tint can apply.
            var previous=RenderTexture.active;
            var rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32);
            var copy=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false){name="ADV.CG.NativeAlpha",filterMode=source.filterMode,wrapMode=TextureWrapMode.Clamp};
            try
            {
                Graphics.Blit(source,rt);RenderTexture.active=rt;
                copy.ReadPixels(new Rect(0,0,source.width,source.height),0,0);copy.Apply();
                var pixels=copy.GetPixels32();
                for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(255,255,255,pixels[i].a);
                copy.SetPixels32(pixels);copy.Apply(false,true);textures[key]=copy;return copy;
            }
            catch{UnityEngine.Object.Destroy(copy);throw;}
            finally{RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);}
        }
        internal static RawImage Logo(Transform parent)
        {
            var image=AdvWidgets.Rect("ADV.StudentAgeLogo",parent,98,985,190,56).gameObject.AddComponent<RawImage>();
            image.texture=Texture("logo-mask.png");image.uvRect=LogoUv;
            image.rectTransform.sizeDelta=new Vector2(190,190*LogoUv.height*image.texture.height/(LogoUv.width*image.texture.width));
            image.color=new Color(1,1,1,.46f);image.raycastTarget=false;return image;
        }
        internal static void Release(){foreach(var t in textures.Values)UnityEngine.Object.Destroy(t);textures.Clear();layout=null;UnityEngine.Object.Destroy(clickSound);UnityEngine.Object.Destroy(toggleSound);UnityEngine.Object.Destroy(hoverSound);clickSound=toggleSound=hoverSound=null;uiAudio=null;}
    }

    internal sealed class AdvSkinButton : MonoBehaviour,IPointerEnterHandler,IPointerExitHandler,IPointerDownHandler,IPointerUpHandler
    {
        string asset;RawImage graphic;bool over,pressed,active;
        internal RectTransform IconRect=>graphic.rectTransform;
        internal static AdvSkinButton Create(string name,string asset,Transform parent,float x,float y,Action click)
        {
            float width=(float)AdvSkin.Layout[asset]["width"];
            var r=AdvWidgets.Rect(name,parent,x,y,width,50);
            var hit=r.gameObject.AddComponent<Image>();hit.color=Color.clear;
            var button=r.gameObject.AddComponent<AdvButton>();button.targetGraphic=hit;button.transition=Selectable.Transition.None;
            var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
            AdvSkin.PrepareAudio();button.onClick.AddListener(()=>{click();});
            var skin=r.gameObject.AddComponent<AdvSkinButton>();skin.asset=asset;
            skin.graphic=AdvWidgets.Rect("Artwork",r,0,0,width,50).gameObject.AddComponent<RawImage>();skin.graphic.raycastTarget=false;
            // Preload all states now; hover never decodes an image.
            foreach(var state in ((JObject)AdvSkin.Layout[asset]["states"]).Properties())AdvSkin.Texture(asset+"_"+state.Name+".png");
            skin.Refresh();return skin;
        }
        internal void SetActive(bool value){if(active==value)return;active=value;Refresh();}
        void Refresh()
        {
            string state=pressed?"on":over?"over":active?"on":"normal";
            if(asset=="hold")state=pressed?(active?"on_on":"on_off"):over?(active?"over_on":"over_off"):active?"normal_on":"normal";
            var s=AdvSkin.Layout[asset]["states"][state];
            graphic.texture=AdvSkin.Texture(asset+"_"+state+".png");
            graphic.rectTransform.anchoredPosition=new Vector2((float)s["ox"],-(float)s["oy"]);
            graphic.rectTransform.sizeDelta=new Vector2((float)s["cw"],(float)s["ch"]);
        }
        public void OnPointerEnter(PointerEventData e){over=true;Refresh();}
        public void OnPointerExit(PointerEventData e){over=false;Refresh();}
        public void OnPointerDown(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=true;Refresh();}}
        public void OnPointerUp(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=false;Refresh();}}
        void OnDisable(){over=pressed=false;if(graphic!=null)Refresh();}
    }
}
