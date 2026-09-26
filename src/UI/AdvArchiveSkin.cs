using System;
using System.IO;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace StudentAgeDialogueSave.UI
{
    internal static class AdvArchiveSkin
    {
        internal static readonly Color Ink=new Color32(42,80,115,255);
        internal static readonly Color[] Themes={new Color32(255,149,184,255),new Color32(79,191,240,255),new Color32(164,224,65,255),new Color32(255,219,53,255)};
        static readonly Dictionary<string,Sprite> sprites=new Dictionary<string,Sprite>();
        static readonly Dictionary<int,Color> detailColors=new Dictionary<int,Color>();
        internal static Color DetailColor(int theme){Art("detail_base__null_",theme);return detailColors[theme];}
        static readonly List<Texture2D> textures=new List<Texture2D>();
        internal static Sprite Background(int theme)
        {
            string key="background"+theme;if(sprites.TryGetValue(key,out var ready))return ready;
            var texture=Load("archive-background-"+new[]{"pink","blue","green","yellow"}[theme]+".png");
            texture.Apply(false,true);ready=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
            sprites[key]=ready;return ready;
        }
        // Original file__bg0 panel, including its printed grey gradient and
        // rounded antialiased corners. Only the surrounding yellow is keyed out.
        internal static Sprite Paper()
        {
            if(sprites.TryGetValue("paper",out var ready))return ready;
            var original=Load("archive-source-background.png");const int w=1760,h=840;
            var pixels=original.GetPixels(80,original.height-120-h,w,h);
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                if((x<24 || x>w-25) && (y<24 || y>h-25))
                {
                    int i=y*w+x;var c=pixels[i];
                    float fringe=Mathf.Max(c.r,c.g,c.b)-Mathf.Min(c.r,c.g,c.b);
                    c.a*=1-Mathf.Clamp01((fringe-.08f)/.14f);pixels[i]=c;
                }
            var t=new Texture2D(w,h,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};textures.Add(t);
            t.SetPixels(pixels);t.Apply(false,true);UnityEngine.Object.Destroy(original);
            ready=Sprite.Create(t,new Rect(0,0,w,h),new Vector2(.5f,.5f),100);sprites["paper"]=ready;return ready;
        }
        static Sprite PageIcon(bool plus,int state)
        {
            string key="page"+plus+state;if(sprites.TryGetValue(key,out var ready))return ready;
            // 40x40 cr=1 entries from the extracted file.json _pgbtn states.
            int[] xs=plus?new[]{374,294,40}:new[]{334,374,0};
            int[] ys=plus?new[]{238,198,314}:new[]{278,198,314};
            if(!sprites.TryGetValue("pageAtlas",out var atlas))
            {
                var texture=Load("archive-source-controls.png");var p=texture.GetPixels32();
                for(int i=0;i<p.Length;i++)if(p[i].r>180 && p[i].g>70 && p[i].b<100)p[i]=new Color32(41,(byte)(137+p[i].g/12),222,p[i].a);
                texture.SetPixels32(p);texture.Apply(false,true);atlas=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);sprites["pageAtlas"]=atlas;
            }
            ready=Sprite.Create(atlas.texture,new Rect(xs[state],atlas.texture.height-ys[state]-40,40,40),new Vector2(.5f,.5f),100);sprites[key]=ready;return ready;
        }
        internal static Button PageButton(string name,bool plus,Transform parent,float x,float y,Action action)
        {
            var root=AdvWidgets.Rect(name,parent,x,y,44,44);var hit=root.gameObject.AddComponent<Image>();hit.color=Color.clear;
            var b=root.gameObject.AddComponent<AdvButton>();b.transition=Selectable.Transition.SpriteSwap;
            var art=AdvWidgets.Rect("Source page icon",root,22,22,44,44).gameObject.AddComponent<Image>();art.rectTransform.pivot=new Vector2(.5f,.5f);art.rectTransform.localEulerAngles=new Vector3(0,0,-90);art.raycastTarget=false;art.sprite=PageIcon(plus,0);
            b.targetGraphic=art;b.spriteState=new SpriteState{highlightedSprite=PageIcon(plus,1),pressedSprite=PageIcon(plus,2),disabledSprite=art.sprite};
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;b.onClick.AddListener(()=>action());return b;
        }
        internal static Sprite InformationTag()
        {
            if(sprites.TryGetValue("informationTag",out var ready))return ready;
            var t=Load("archive-information-tag.png");var p=t.GetPixels32();int left=t.width,right=0,low=t.height,high=0;
            for(int y=0;y<t.height;y++)for(int x=0;x<t.width;x++)if(p[y*t.width+x].a>100){left=Math.Min(left,x);right=Math.Max(right,x);low=Math.Min(low,y);high=Math.Max(high,y);}
            ready=Sprite.Create(t,new Rect(left,low,right-left+1,high-low+1),new Vector2(.5f,.5f),100);sprites["informationTag"]=ready;t.Apply(false,true);return ready;
        }
        internal static Button NavigationButton(string name,string direction,Transform parent,float x,float y,Action action)
        {
            PageIcon(false,0);var atlas=sprites["pageAtlas"].texture;
            var coordinates=new Dictionary<string,int[]>{
                {"top",new[]{414,238,294,238,685,296}}, {"up10",new[]{414,278,334,198,476,284}},
                {"up",new[]{374,278,294,278,294,80}}, {"dw",new[]{334,238,334,158,374,80}},
                {"dw10",new[]{414,158,374,158,414,80}}, {"btm",new[]{414,198,294,158,334,80}}};
            var states=new Sprite[3];var c=coordinates[direction];
            for(int i=0;i<3;i++){string key="nav"+direction+i;if(!sprites.TryGetValue(key,out states[i])){states[i]=Sprite.Create(atlas,new Rect(c[i*2],atlas.height-c[i*2+1]-40,40,40),new Vector2(.5f,.5f),100);sprites[key]=states[i];}}
            var root=AdvWidgets.Rect(name,parent,x,y,42,42);var hit=root.gameObject.AddComponent<Image>();hit.color=Color.clear;
            var art=AdvWidgets.Rect("Source navigation icon",root,21,21,40,40).gameObject.AddComponent<Image>();art.rectTransform.pivot=new Vector2(.5f,.5f);art.rectTransform.localEulerAngles=new Vector3(0,0,-90);art.sprite=states[0];art.raycastTarget=false;
            var b=root.gameObject.AddComponent<AdvButton>();b.targetGraphic=art;b.transition=Selectable.Transition.SpriteSwap;b.spriteState=new SpriteState{highlightedSprite=states[1],pressedSprite=states[2],disabledSprite=states[0]};
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;b.onClick.AddListener(()=>action());return b;
        }
        static Texture2D Load(string name)
        {
            using(var input=typeof(AdvArchiveSkin).Assembly.GetManifestResourceStream("DialogueSave.Skin."+name))
            using(var data=new MemoryStream()){input.CopyTo(data);var t=new Texture2D(2,2,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};ImageConversion.LoadImage(t,data.ToArray());textures.Add(t);return t;}
        }
        internal static Sprite Art(string name,int theme=1)
        {
            string key=name+theme;if(sprites.TryGetValue(key,out var ready))return ready;
            var t=Load("archive-"+name+".png");var p=t.GetPixels32();int w=t.width,h=t.height;
            // Source atlas sprites carry their cr=1 rotation. Restore only that
            // packing rotation; preserve border widths and original cutout geometry.
            bool rotated=name=="detail_base__null_" || name=="detail_over__null_" || name=="item_over" || name=="item_on";
            var ink=Themes[theme];
            for(int i=0;i<p.Length;i++)
            {
                Color c=p[i];if(c.g>c.b+.045f && c.g>c.r*.98f)
                {float saturation=Mathf.Clamp01((c.g-c.b)/Mathf.Max(.01f,c.g));Color v=Color.Lerp(Color.white,ink,saturation);v*=Mathf.Max(c.r,c.g);v.a=c.a;p[i]=v;}
            }
            if(rotated){var q=new Color32[p.Length];for(int y=0;y<h;y++)for(int x=0;x<w;x++)q[(w-1-x)*h+y]=p[y*w+x];UnityEngine.Object.Destroy(t);t=new Texture2D(h,w,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear};textures.Add(t);p=q;}
            if(name=="detail_base__null_")
            {
                detailColors[theme]=p[(t.height-100)*t.width+100];
                foreach(int top in new[]{426,488,550})for(int yy=top;yy<top+42;yy++)for(int xx=20;xx<158;xx++)
                    p[(t.height-1-yy)*t.width+xx]=p[(t.height-1-yy)*t.width+160];
            }
            t.SetPixels32(p);t.Apply(false,true);
            ready=Sprite.Create(t,new Rect(0,0,t.width,t.height),new Vector2(.5f,.5f),100);sprites[key]=ready;return ready;
        }
        internal static Sprite Cursor(int theme)
        {
            string key="cursor"+theme;if(sprites.TryGetValue(key,out var ready))return ready;
            var t=Load("archive-cursor-atlas.png");var colors=t.GetPixels32();Color ink=Themes[theme];
            for(int i=0;i<colors.Length;i++){Color c=colors[i];if(c.r>.7f && c.g>.3f && c.b<.4f){var value=Color.Lerp(Ink,ink,.8f);value.a=c.a;colors[i]=value;}}
            t.SetPixels32(colors);t.Apply(false,true);ready=Sprite.Create(t,new Rect(276,t.height-504-248,272,248),new Vector2(.5f,.5f),100);sprites[key]=ready;return ready;
        }
        internal static Image Letter(int row,Transform parent,float x,float y,float w,float h)
        {
            var art=AdvWidgets.Rect("Archive lettering",parent,x,y,w,h).gameObject.AddComponent<Image>();art.sprite=LetterSprite(row);art.color=Ink;art.preserveAspect=true;art.raycastTarget=false;return art;
        }
        static Sprite[] letters;
        internal static Sprite LetterSprite(int row)
        {
            if(letters!=null)return letters[row];
            var t=Load("archive-lettering.png");var p=t.GetPixels32();letters=new Sprite[6];
            for(int band=0;band<6;band++)
            {
                int top=t.height-band*t.height/6,bottom=t.height-(band+1)*t.height/6,left=t.width,right=0,lo=top,hi=bottom;
                for(int yy=bottom;yy<top;yy++)for(int xx=0;xx<t.width;xx++)
                {
                    var c=p[yy*t.width+xx];if(c.r<150 && c.g<170){left=Math.Min(left,xx);right=Math.Max(right,xx);lo=Math.Min(lo,yy);hi=Math.Max(hi,yy);}
                    float alpha=1-Mathf.Min(c.r,c.g)/255f;p[yy*t.width+xx]=new Color(1,1,1,Mathf.Clamp01((alpha-.08f)/.78f));
                }
                letters[band]=Sprite.Create(t,new Rect(left,lo,right-left+1,hi-lo+1),new Vector2(.5f,.5f),100);sprites["letter"+band]=letters[band];
            }
            t.SetPixels32(p);t.Apply(false,true);return letters[row];
        }
        internal static Button Button(string id,Transform parent,TMP_FontAsset font,string label,float x,float y,float w,float h,Action action,int kind=0)
        {
            var image=AdvSettingsSkin.Control(id,parent,kind,x,y,w,h);var b=image.gameObject.AddComponent<AdvButton>();b.targetGraphic=image;AdvSettingsSkin.Style(b,kind);
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;
            var brush=kind==1?AdvSettingsSkin.TabBrush(image.transform,w,h,label.Length):null;
            var text=AdvWidgets.Label("Caption",image.transform,font,label,6,0,w-12,h,kind==1?23:24,Ink);text.alignment=TextAlignmentOptions.Center;
            var tint=image.gameObject.AddComponent<SettingsButtonInk>();tint.Text=text;tint.Brush=brush;if(kind==1)text.fontStyle=FontStyles.Bold;tint.Choice=kind==0;tint.TopTab=kind==1;
            b.onClick.AddListener(()=>action());return b;
        }
        internal static void Select(Button b,bool selected,int kind=0){AdvSettingsSkin.Style(b,kind,selected);b.GetComponent<SettingsButtonInk>()?.SetSelected(selected);}
        internal static System.Collections.IEnumerator Warm()
        {
            Paper();yield return null;InformationTag();yield return null;
            for(int i=0;i<3;i++){PageIcon(false,i);PageIcon(true,i);yield return null;}
            for(int theme=0;theme<4;theme++){Background(theme);yield return null;}
            for(int i=0;i<6;i++){LetterSprite(i);yield return null;}
            for(int theme=0;theme<4;theme++)
            {foreach(string name in new[]{"item_off","item_over","item_on","detail_base__null_"}){Art(name,theme);yield return null;}Cursor(theme);yield return null;}
        }
        internal static void Release(){letters=null;foreach(var s in sprites.Values)UnityEngine.Object.Destroy(s);sprites.Clear();foreach(var t in textures)if(t!=null)UnityEngine.Object.Destroy(t);textures.Clear();}
    }
    internal sealed class ArchiveRailAudio:MonoBehaviour,IPointerEnterHandler,IPointerDownHandler
    {
        public void OnPointerEnter(PointerEventData e){AdvSkin.PlayHover();}
        public void OnPointerDown(PointerEventData e){if(e.button==PointerEventData.InputButton.Left)AdvSkin.PlayClick(false);}
    }
    // The native file__bg0 bakes the drop shadow into the coloured scenery.
    // Fit its subtle edge profile separately: 2px down, radius 16, sigma ~4.
    // At the outline opacity is .08 (half of the hidden .16 core), not .16.
    internal sealed class ArchivePaperShadow:MaskableGraphic
    {
        static readonly float[] Opacity={.16f,.159f,.156f,.149f,.135f,.111f,.08f,.049f,.025f,.011f,.004f,.001f,0};
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();var rect=rectTransform.rect;
            const int steps=12,points=4*(steps+1);const float radius=16;
            mesh.AddVert(new Vector3(rect.center.x,rect.center.y),new Color(0,0,0,Opacity[0]),Vector2.zero);
            for(int ring=0;ring<Opacity.Length;ring++)
            {
                float offset=-12+ring*2;
                for(int corner=0;corner<4;corner++)
                {
                    float cx=corner==0 || corner==3?rect.xMax-radius:rect.xMin+radius;
                    float cy=corner<2?rect.yMax-radius:rect.yMin+radius;
                    for(int point=0;point<=steps;point++)
                    {
                        float angle=(corner*90f+point*90f/steps)*Mathf.Deg2Rad;
                        mesh.AddVert(new Vector3(cx+Mathf.Cos(angle)*(radius+offset),cy+Mathf.Sin(angle)*(radius+offset)),new Color(0,0,0,Opacity[ring]),Vector2.zero);
                    }
                }
                int start=1+ring*points;
                for(int point=0;point<points;point++)
                {
                    int next=(point+1)%points;
                    if(ring==0)mesh.AddTriangle(0,start+point,start+next);
                    else
                    {
                        int inner=start-points;
                        mesh.AddTriangle(inner+point,start+point,start+next);
                        mesh.AddTriangle(inner+point,start+next,inner+next);
                    }
                }
            }
        }
    }
    internal sealed class ArchiveSlotHover:MonoBehaviour,IPointerEnterHandler,IPointerExitHandler,IPointerDownHandler,IPointerUpHandler
    {
        internal Image Cursor,Background;internal Sprite Idle,Over,Down;internal bool Selected;internal Action<bool> Hover;bool over,pressed;
        internal void Refresh(){if(Background!=null)Background.sprite=pressed?Down:over?Over:Idle;Cursor.enabled=over || pressed || Selected;Cursor.color=pressed?new Color(.82f,.9f,1,1):Color.white;}
        public void OnPointerEnter(PointerEventData e){over=true;Refresh();Hover?.Invoke(true);}
        public void OnPointerExit(PointerEventData e){over=false;Refresh();Hover?.Invoke(false);}
        public void OnPointerDown(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=true;Refresh();}}
        public void OnPointerUp(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=false;Refresh();}}
        void OnDisable(){over=pressed=false;if(Cursor!=null)Refresh();}
    }
}
