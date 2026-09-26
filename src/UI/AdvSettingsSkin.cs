using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    // Redrawn bitmap assets, not procedural approximations of the source skin.
    internal static class AdvSettingsSkin
    {
        static Texture2D tabBrushArt,backdrop,controls,card,headingIcon,lettering,captionMask,tracks,helpArt,conditionArt,sourceControls,choiceHover;
        static readonly Dictionary<string,Sprite> captions=new Dictionary<string,Sprite>();
        static Sprite tabBrushSprite,titleSprite,choiceSelectedHover,tabNotch;
        static readonly Sprite[,] states=new Sprite[4,3];
        static Sprite cardSprite,headingSprite;
        static Sprite rail,helpSprite,conditionPaper;
        static readonly List<Sprite> sprites=new List<Sprite>();
        static readonly List<Texture2D> extraLettering=new List<Texture2D>();
        static readonly HashSet<Texture2D> pendingLettering=new HashSet<Texture2D>();
        static readonly Dictionary<string,Texture2D> imported=new Dictionary<string,Texture2D>();
        internal static System.Collections.IEnumerator Warm()
        {
            string[] names={"settings-background.png","settings-controls.png","settings-source-frame.png","settings-heading-icon.png","settings-lettering.png","settings-tracks.png","settings-help.png","choice-condition.png","settings-source-controls.png","settings-tab-brush.png"};
            foreach(string name in names)
            {
                yield return null;
                Load(name,name!="settings-background.png" && name!="settings-source-frame.png" && name!="settings-heading-icon.png" && name!="settings-source-controls.png");
            }
            Prepare();
            yield return WarmCaption("返回对话");
            yield return WarmCaption("恢复默认");
        }
        static Texture2D ReadTexture(string name)
        {
            byte[] bytes;
            using(var stream=typeof(AdvSettingsSkin).Assembly.GetManifestResourceStream("DialogueSave.Skin."+name))
            using(var data=new MemoryStream()){stream.CopyTo(data);bytes=data.ToArray();}
            var texture=new Texture2D(2,2,TextureFormat.RGBA32,false){name=name,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            ImageConversion.LoadImage(texture,bytes);
            return texture;
        }
        static Texture2D Load(string name,bool keyed)
        {
            if(imported.TryGetValue(name,out var cached))return cached;
            var texture=ReadTexture(name);
            if(keyed)
            {
                // Asset import: remove the generator's solid magenta matte and
                // unpremultiply edge pixels. This never runs in animation/update.
                var pixels=texture.GetPixels32();
                bool hasAlpha=false;
                for(int i=0;i<pixels.Length;i++)if(pixels[i].a<250){hasAlpha=true;break;}
                for(int i=0;i<pixels.Length;i++)
                {
                    if(hasAlpha)break; // Preserve real RGBA exports as delivered.
                    var p=pixels[i];float r=p.r/255f,g=p.g/255f,b=p.b/255f;
                    float a=Mathf.Clamp01(1-Mathf.Min(r,b)+g);
                    // Generated matte can contain faint paper/noise. Remove it
                    // rather than letting a rectangular haze become visible.
                    if(a<=.25f){pixels[i]=Color.clear;continue;}
                    pixels[i]=new Color(Mathf.Clamp01((r-1+a)/a),Mathf.Clamp01(g/a),Mathf.Clamp01((b-1+a)/a),(a-.25f)/.75f);
                }
                texture.SetPixels32(pixels);texture.Apply();
            }
            imported[name]=texture;return texture;
        }
        static string CaptionFile(string caption)=>caption=="恢复默认"?"lettering-restore-defaults.png":caption=="返回对话"?"lettering-return-dialogue.png":null;
        static System.Collections.IEnumerator WarmCaption(string caption)
        {
            if(captions.ContainsKey(caption))yield break;
            yield return null;
            // Own the unfinished texture privately: a user may open the page
            // before warm-up completes and take the normal synchronous path.
            var texture=ReadTexture(CaptionFile(caption));
            pendingLettering.Add(texture);bool published=false;
            try
            {
                var pixels=texture.GetPixels32();bool hasAlpha=false;
                for(int i=0;i<pixels.Length;i++)
                {
                    if(pixels[i].a<250){hasAlpha=true;break;}
                    if((i&16383)==16383)yield return null;
                }
                int width=texture.width,height=texture.height;
                int left=width,bottom=height,right=0,top=0;
                for(int i=0;i<pixels.Length;i++)
                {
                    if((i&16383)==0)
                    {
                        yield return null;
                        if(captions.ContainsKey(caption))yield break;
                    }
                    var p=pixels[i];
                    if(!hasAlpha)
                    {
                        float r=p.r/255f,g=p.g/255f,b=p.b/255f,a=Mathf.Clamp01(1-Mathf.Min(r,b)+g);
                        p=a<=.25f?(Color32)Color.clear:(Color32)new Color(Mathf.Clamp01((r-1+a)/a),Mathf.Clamp01(g/a),Mathf.Clamp01((b-1+a)/a),(a-.25f)/.75f);
                    }
                    float bright=Mathf.Max(p.r,Mathf.Max(p.g,p.b))/255f;
                    pixels[i]=new Color(1,1,1,p.a/255f*Mathf.Clamp01((.65f-bright)/.25f));
                    if(pixels[i].a>80)
                    {
                        int x=i%width,y=i/width;
                        left=Math.Min(left,x);right=Math.Max(right,x);bottom=Math.Min(bottom,y);top=Math.Max(top,y);
                    }
                }
                texture.SetPixels32(pixels);texture.Apply(false,true);
                var bounds=Rect.MinMaxRect(Math.Max(0,left-2),Math.Max(0,bottom-2),Math.Min(width,right+3),Math.Min(height,top+3));
                captions[caption]=Make(texture,bounds,Vector4.zero);
                imported[CaptionFile(caption)]=texture;extraLettering.Add(texture);
                published=true;
            }
            finally {pendingLettering.Remove(texture);if(!published && texture!=null)UnityEngine.Object.Destroy(texture);}
        }
        static Rect Bounds(Color32[] pixels,int width,RectInt cell,byte threshold=80)
        {
            int left=cell.xMax,bottom=cell.yMax,right=cell.xMin,top=cell.yMin;
            for(int y=cell.yMin;y<cell.yMax;y++)for(int x=cell.xMin;x<cell.xMax;x++)if(pixels[y*width+x].a>threshold)
            {left=Math.Min(left,x);right=Math.Max(right,x);bottom=Math.Min(bottom,y);top=Math.Max(top,y);}
            return Rect.MinMaxRect(Math.Max(cell.xMin,left-2),Math.Max(cell.yMin,bottom-2),Math.Min(cell.xMax,right+3),Math.Min(cell.yMax,top+3));
        }
        // Atlas neighbours sit directly beside some control crops; bilinear sampling of a
        // sliced edge would smear them into full-length lines. Copy each crop with clear padding.
        static Sprite Isolated(Texture2D texture,Rect rect,Vector4 border)
        {
            const int pad=2;int x=(int)rect.x,y=(int)rect.y,w=(int)rect.width,h=(int)rect.height;
            var copy=new Texture2D(w+pad*2,h+pad*2,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            copy.SetPixels32(new Color32[(w+pad*2)*(h+pad*2)]);copy.SetPixels(pad,pad,w,h,texture.GetPixels(x,y,w,h));copy.Apply(false,true);
            extraLettering.Add(copy);return Make(copy,new Rect(pad,pad,w,h),border);
        }
        static Sprite Make(Texture2D texture,Rect rect,Vector4 border)
        {var sprite=Sprite.Create(texture,rect,new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect,border);sprites.Add(sprite);return sprite;}
        internal static void Prepare()
        {
            if(backdrop!=null)return;
            backdrop=Load("settings-background.png",false);controls=Load("settings-controls.png",true);card=Load("settings-source-frame.png",false);
            headingIcon=Load("settings-heading-icon.png",false);
            lettering=Load("settings-lettering.png",true);tracks=Load("settings-tracks.png",true);
            helpArt=Load("settings-help.png",true);
            var helpBounds=Bounds(helpArt.GetPixels32(),helpArt.width,new RectInt(0,0,helpArt.width,helpArt.height));
            helpSprite=Make(helpArt,helpBounds,new Vector4(helpBounds.height*1.6f,0,helpBounds.height*.35f,0));
            conditionArt=Load("choice-condition.png",true);
            var conditionBounds=Bounds(conditionArt.GetPixels32(),conditionArt.width,new RectInt(0,0,conditionArt.width,conditionArt.height),32);
            conditionPaper=Make(conditionArt,conditionBounds,Vector4.one*conditionBounds.height*.18f);
            sourceControls=Load("settings-source-controls.png",false);
            var pixels=controls.GetPixels32();int cw=controls.width/3,ch=controls.height/4;
            for(int row=0;row<4;row++)
            {
                Rect union=new Rect();
                for(int col=0;col<3;col++)
                {
                    var cell=new RectInt(col*cw,(3-row)*ch,cw,ch);var bounds=Bounds(pixels,controls.width,cell);bounds.position-=new Vector2(cell.x,cell.y);
                    union=col==0?bounds:Rect.MinMaxRect(Mathf.Min(union.xMin,bounds.xMin),Mathf.Min(union.yMin,bounds.yMin),Mathf.Max(union.xMax,bounds.xMax),Mathf.Max(union.yMax,bounds.yMax));
                }
                // Keep circular thumbs circular even though hover rays extend sideways.
                if(row==3){float side=Mathf.Max(union.width,union.height);union=new Rect(union.center-new Vector2(side,side)*.5f,new Vector2(side,side));}
                for(int col=0;col<3;col++){var r=union;r.position+=new Vector2(col*cw,(3-row)*ch);states[row,col]=Make(controls,r,row==0?new Vector4(48,40,48,40):row==2?new Vector4(union.width*.32f,0,union.height*.45f,0):Vector4.zero);}
            }
            // Exact page0 off/over/on rectangles from option_jp.json.
            // Slice the capsule independently of its notch so widening a tab
            // stretches only the straight span, never the endcaps or pointer.
            var original=sourceControls.GetPixels32();
            states[1,0]=Make(sourceControls,new Rect(332,sourceControls.height-515-50,130,50),new Vector4(25,24,25,24));
            states[1,1]=Make(sourceControls,new Rect(332,sourceControls.height-462-50,130,50),new Vector4(25,24,25,24));
            states[1,2]=Make(sourceControls,new Rect(374,sourceControls.height-323-50,130,50),new Vector4(25,24,25,24));
            tabNotch=Make(sourceControls,new Rect(374+53,sourceControls.height-323-55,24,10),Vector4.zero);
            // Use the original three choice states: outline, filled hover, selected rim.
            for(int i=0;i<original.Length;i++)
            {
                var pixel=original[i];
                if(pixel.r>180 && pixel.g>70 && pixel.b<100)
                    original[i]=new Color32(41,(byte)(137+pixel.g/12),222,pixel.a);
            }
            sourceControls.SetPixels32(original);sourceControls.Apply();
            int[] sourceY={61,8,114};
            for(int state=0;state<3;state++)states[0,state]=Isolated(sourceControls,new Rect(80,sourceControls.height-sourceY[state]-53,308,53),new Vector4(30,24,30,24));
            choiceHover=new Texture2D(308,53,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            var selectedPixels=sourceControls.GetPixels(80,sourceControls.height-114-53,308,53);
            for(int i=0;i<selectedPixels.Length;i++)if(selectedPixels[i].b>.65f && selectedPixels[i].r<.3f)selectedPixels[i]=new Color(.12f,.65f,.96f,selectedPixels[i].a);
            choiceHover.SetPixels(selectedPixels);choiceHover.Apply();choiceSelectedHover=Make(choiceHover,new Rect(0,0,308,53),new Vector4(30,24,30,24));
            var trackPixels=tracks.GetPixels32();
            var normal=Bounds(trackPixels,tracks.width,new RectInt(0,0,tracks.width,tracks.height));
            rail=Make(tracks,normal,new Vector4(normal.height,normal.height*.5f,normal.height,normal.height*.5f));
            // Exact first panel from the extracted two-column/five-row frame.
            // Keep the 54px header and rounded rims at source pixel size.
            cardSprite=Make(card,new Rect(0,card.height-164,868,164),new Vector4(18,18,18,54));
            headingSprite=Make(headingIcon,new Rect(0,0,headingIcon.width,headingIcon.height),Vector4.zero);
            var letters=lettering.GetPixels32();
            titleSprite=Make(lettering,Bounds(letters,lettering.width,new RectInt(0,lettering.height-300,lettering.width,300)),Vector4.zero);
            // Button lettering uses only the original dark glyph coverage.
            // Discard pale outlines/offset decorations and tint the mask through
            // the same UI state callbacks as live text (no per-frame decoding).
            var mask=new Color32[letters.Length];
            for(int i=0;i<letters.Length;i++)
            {
                var p=letters[i];float brightest=Mathf.Max(p.r,Mathf.Max(p.g,p.b))/255f;
                mask[i]=new Color(1,1,1,p.a/255f*Mathf.Clamp01((.65f-brightest)/.25f));
            }
            captionMask=new Texture2D(lettering.width,lettering.height,TextureFormat.RGBA32,false){name="Settings lettering coverage",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            captionMask.SetPixels32(mask);captionMask.Apply();
            string[] words={"游戏","图像","音频","模组配置","隐藏","显示","原版","ADV","仅已读文本","全部文本","取消","应用并返回"};
            int[] tops={300,540,780},bottoms={500,750,970};
            for(int i=0;i<words.Length;i++)
            {
                int row=i/4;var cell=new RectInt(i%4*384,lettering.height-bottoms[row],384,bottoms[row]-tops[row]);
                captions[words[i]]=Make(captionMask,Bounds(mask,lettering.width,cell),Vector4.zero);
            }
            foreach(var texture in new[]{controls,card,headingIcon,backdrop,lettering,captionMask,tracks,helpArt,conditionArt,sourceControls,choiceHover})texture.Apply(false,true);
        }
        static Sprite ExtraCaption(string caption)
        {
            if(captions.TryGetValue(caption,out var existing))return existing;
            string file=CaptionFile(caption);
            if(file==null)return null;
            var texture=Load(file,true);var pixels=texture.GetPixels32();
            // Import a tintable dark-glyph mask; discard any pale matte without
            // changing the artist's letter outlines or rerendering it each frame.
            for(int i=0;i<pixels.Length;i++)
            {
                var p=pixels[i];float bright=Mathf.Max(p.r,Mathf.Max(p.g,p.b))/255f;
                pixels[i]=new Color(1,1,1,p.a/255f*Mathf.Clamp01((.65f-bright)/.25f));
            }
            texture.SetPixels32(pixels);texture.Apply();
            var sprite=Make(texture,Bounds(pixels,texture.width,new RectInt(0,0,texture.width,texture.height)),Vector4.zero);
            texture.Apply(false,true);extraLettering.Add(texture);captions[caption]=sprite;return sprite;
        }
        internal static RawImage Background(Transform parent)
        {Prepare();var image=AdvWidgets.Rect("Redrawn campus backdrop",parent,0,0,1920,1080).gameObject.AddComponent<RawImage>();image.texture=backdrop;image.raycastTarget=true;return image;}
        internal static Image Card(string name,Transform parent,float x,float y,float w,float h)
        {
            Prepare();var image=AdvWidgets.Rect(name,parent,x,y,w,h).gameObject.AddComponent<Image>();
            image.sprite=cardSprite;image.type=Image.Type.Sliced;image.pixelsPerUnitMultiplier=1;image.raycastTarget=false;return image;
        }
        internal static void HeadingIcon(Transform parent)
        {var image=AdvWidgets.Rect("Source heading icon",parent,22,12,60,38).gameObject.AddComponent<Image>();image.sprite=headingSprite;image.raycastTarget=false;}
        internal static Image Control(string name,Transform parent,int row,float x,float y,float w,float h)
        {Prepare();var image=AdvWidgets.Rect(name,parent,x,y,w,h).gameObject.AddComponent<Image>();image.sprite=states[row,0];if(row==0 || row==1 || row==2){image.type=Image.Type.Sliced;image.pixelsPerUnitMultiplier=row==1?50/h:row==2?image.sprite.rect.height/h:1;}
            if(row==1){var notch=AdvWidgets.Rect("Source tab notch",image.transform,(w-24*h/50)/2,h-5*h/50,24*h/50,10*h/50).gameObject.AddComponent<Image>();notch.sprite=tabNotch;notch.raycastTarget=false;notch.gameObject.SetActive(false);}return image;}
        internal static Image TabBrush(Transform parent,float w,float h,int characters)
        {
            if(tabBrushArt==null){tabBrushArt=Load("settings-tab-brush.png",true);tabBrushSprite=Make(tabBrushArt,Bounds(tabBrushArt.GetPixels32(),tabBrushArt.width,new RectInt(0,0,tabBrushArt.width,tabBrushArt.height)),Vector4.zero);tabBrushArt.Apply(false,true);}
            float width=Mathf.Min(w-40,characters*28+66);
            var image=AdvWidgets.Rect("White marker backing",parent,(w-width)/2,(h-43)/2,width,43).gameObject.AddComponent<Image>();
            image.sprite=tabBrushSprite;image.raycastTarget=false;return image;
        }
        internal static Image Help(Transform parent)
        {var image=AdvWidgets.Rect("Campus information strip",parent,0,0,1040,78).gameObject.AddComponent<Image>();image.sprite=helpSprite;image.type=Image.Type.Sliced;image.pixelsPerUnitMultiplier=helpSprite.rect.height/78;image.raycastTarget=false;return image;}
        internal static Image ConditionHelp(Transform parent)
        {
            Prepare();
            var image=AdvWidgets.Rect("Dark condition tooltip",parent,0,0,510,100).gameObject.AddComponent<Image>();
            image.sprite=conditionPaper;image.type=Image.Type.Sliced;
            image.pixelsPerUnitMultiplier=conditionPaper.rect.height/100;image.raycastTarget=false;
            return image;
        }
        internal static Image Rail(string name,Transform parent,float x,float y,float w,float h)
        {var image=Control(name,parent,0,x,y,w,h);image.sprite=rail;image.type=Image.Type.Sliced;image.pixelsPerUnitMultiplier=image.sprite.rect.height/h;image.raycastTarget=false;return image;}
        internal static Image Lettering(string caption,Transform parent,float x,float y,float w,float h)
        {
            Prepare();Sprite sprite;
            if(caption=="System Setting")sprite=titleSprite;else {sprite=ExtraCaption(caption);if(sprite==null)return null;}
            float scale=Mathf.Min(w/sprite.rect.width,h/sprite.rect.height),aw=sprite.rect.width*scale,ah=sprite.rect.height*scale;
            var image=AdvWidgets.Rect("Artwork."+caption,parent,x+(w-aw)*.5f,y+(h-ah)*.5f,aw,ah).gameObject.AddComponent<Image>();
            image.sprite=sprite;image.color=caption=="System Setting"?Color.white:new Color32(26,49,77,255);image.raycastTarget=false;return image;
        }
        internal static void Style(Selectable button,int row,bool selected=false)
        {
            if(row==1){var notch=button.transform.Find("Source tab notch");if(notch!=null)notch.gameObject.SetActive(selected);}
            var image=(Image)button.targetGraphic;image.sprite=states[row,selected?2:0];image.color=Color.white;
            button.transition=Selectable.Transition.SpriteSwap;
            button.spriteState=new SpriteState{highlightedSprite=row==0 && selected?choiceSelectedHover:states[row,row==1 && selected?2:1],pressedSprite=row==0?states[0,selected?2:1]:states[row,2],selectedSprite=states[row,selected?2:0],disabledSprite=states[row,0]};
        }
        internal static void Release()
        {foreach(var texture in pendingLettering)if(texture!=null)UnityEngine.Object.Destroy(texture);pendingLettering.Clear();ReleasePublished();}
        static void ReleasePublished()
        {foreach(var texture in imported.Values)if(texture!=null)UnityEngine.Object.Destroy(texture);imported.Clear();foreach(var texture in extraLettering)UnityEngine.Object.Destroy(texture);extraLettering.Clear();foreach(var sprite in sprites)UnityEngine.Object.Destroy(sprite);sprites.Clear();captions.Clear();foreach(var texture in new[]{tabBrushArt,backdrop,controls,card,headingIcon,lettering,captionMask,tracks,helpArt,conditionArt,sourceControls,choiceHover})if(texture!=null)UnityEngine.Object.Destroy(texture);tabBrushArt=backdrop=controls=card=headingIcon=lettering=captionMask=tracks=helpArt=conditionArt=sourceControls=choiceHover=null;cardSprite=headingSprite=titleSprite=null;}
    }
}
