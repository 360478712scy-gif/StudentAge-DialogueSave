using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using Newtonsoft.Json.Linq;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using StudentAgeDialogueSave.GameIntegration;

namespace StudentAgeDialogueSave.UI
{
    // Continuous history with only the visible rows instantiated. Native portraits
    // use their historical clothing, never the character's current wardrobe.
    internal sealed class AdvBacklog : MonoBehaviour
    {
        const float Width=1560,ViewportHeight=810,TextWidth=1280;
        List<TalkData> records;
        DialogueHistory history;
        TMP_FontAsset font;
        Action<int> rollback;
        RectTransform content;
        ScrollRect scroll;
        float[] heights,offsets;
        float lastPosition=float.NaN;
        readonly Dictionary<int,RectTransform> visible=new Dictionary<int,RectTransform>();
        readonly Dictionary<int,int> checkpoints=new Dictionary<int,int>();

        internal void Build(Transform parent,TMP_FontAsset readingFont,List<TalkData> data,
            DialogueHistory savedHistory,Action<int> jump,Action close)
        {
            history=savedHistory;font=readingFont;rollback=jump;
            records=new List<TalkData>();
            var native=data??new List<TalkData>();
            var byLine=history.Entries.Select((entry,index)=>new{entry,index}).ToLookup(x=>x.entry.NativeCount-1);
            for(int i=0;i<native.Count;i++)
            {
                int row=records.Count;records.Add(native[i]);
                foreach(var item in byLine[i])
                {
                    if(!item.entry.IsOption){checkpoints[row]=item.index;continue;}
                    checkpoints[records.Count]=item.index;
                    records.Add(new TalkData{talkId=item.entry.TalkId,roleName="选项",content=item.entry.Text});
                }
            }
            var tint=new Color(230f/255,228f/255,222f/255,.86f);
            var paper=AdvWidgets.Box("History fullscreen",parent,0,0,1920,1080,tint,true);
            AdvWidgets.Fill(paper.rectTransform);
            var frame=AdvWidgets.Rect("History layout",parent,0,0,1920,1080);
            frame.anchorMin=frame.anchorMax=frame.pivot=new Vector2(.5f,.5f);frame.anchoredPosition=Vector2.zero;
            AdvWidgets.Box("Binding",frame,164,48,4,46,AdvWidgets.Gold);
            AdvWidgets.Label("Title",frame,font,"对话日志",190,40,600,60,36,AdvWidgets.Ink);
            AdvWidgets.Box("Header rule",frame,164,117,1580,1,new Color(.74f,.63f,.45f,.35f));
            var viewport=AdvWidgets.Rect("History viewport",frame,172,140,Width,ViewportHeight);
            viewport.gameObject.AddComponent<RectMask2D>();
            var hit=viewport.gameObject.AddComponent<Image>();hit.color=new Color(1,1,1,.001f);
            scroll=viewport.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.vertical=true;
            scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=42;
            scroll.decelerationRate=.08f;scroll.viewport=viewport;
            content=AdvWidgets.Rect("History content",viewport,0,0,Width,ViewportHeight);scroll.content=content;
            var rail=AdvWidgets.Box("History scrollbar",frame,1766,198,6,694,new Color(.72f,.65f,.53f,.18f),true);
            var handle=AdvWidgets.Box("Handle",rail.transform,0,0,6,60,AdvWidgets.Gold,true);
            AdvWidgets.Fill(handle.rectTransform);
            var bar=rail.gameObject.AddComponent<Scrollbar>();bar.handleRect=handle.rectTransform;bar.targetGraphic=handle;
            bar.direction=Scrollbar.Direction.BottomToTop;var nav=bar.navigation;nav.mode=Navigation.Mode.None;bar.navigation=nav;
            scroll.verticalScrollbar=bar;
            EndButton(frame,"ADV.HistoryTop",140,11,()=>{scroll.StopMovement();scroll.verticalNormalizedPosition=1;Refresh(false);});
            EndButton(frame,"ADV.HistoryBottom",908,12,()=>{scroll.StopMovement();scroll.verticalNormalizedPosition=0;Refresh(true);});
            AdvWidgets.Box("Footer rule",frame,164,979,1580,1,new Color(.74f,.63f,.45f,.35f));
            var back=AdvWidgets.Button("ADV.CloseHistory",frame,font,"返回对话",1480,997,264,54,close,true);
            back.GetComponentInChildren<TextMeshProUGUI>().fontSize=24;
            heights=new float[records.Count];offsets=new float[records.Count+1];
            for(int i=0;i<records.Count;i++)
            {
                string text=records[i].content??"";
                int lines=1;foreach(char c in text)if(c=='\n')lines++;
                heights[i]=Mathf.Max(216,146+Mathf.Max(lines,(text.Length+44)/45)*39);
            }
            Recalculate();content.anchoredPosition=new Vector2(0,Mathf.Max(0,offsets[records.Count]-ViewportHeight));
            Refresh(true);
        }
        void EndButton(Transform frame,string name,float y,int symbol,Action action)
        {
            var button=AdvWidgets.Button(name,frame,font,"",1748,y,42,42,action);
            var icon=AdvWidgets.Rect("Symbol",button.transform,6,6,30,30).gameObject.AddComponent<AdvIcon>();
            icon.Symbol=symbol;icon.color=AdvWidgets.Blue;icon.raycastTarget=false;
        }
        void Update(){if(content!=null && Math.Abs(content.anchoredPosition.y-lastPosition)>.5f)Refresh(false);}
        void Recalculate()
        {
            for(int i=0;i<heights.Length;i++)offsets[i+1]=offsets[i]+heights[i];
            content.sizeDelta=new Vector2(Width,Mathf.Max(ViewportHeight,offsets[heights.Length]));
        }
        int RowAt(float y)
        {
            int low=0,high=records.Count;
            while(low<high){int mid=(low+high)/2;if(offsets[mid+1]<y)low=mid+1;else high=mid;}
            return low;
        }
        void Refresh(bool bottom)
        {
            float y=Mathf.Max(0,content.anchoredPosition.y);
            int anchor=RowAt(y),start=RowAt(Mathf.Max(0,y-220)),end=Math.Min(records.Count,RowAt(y+ViewportHeight+220)+1);
            float within=anchor<records.Count?y-offsets[anchor]:0;
            foreach(int i in visible.Keys.Where(i=>i<start || i>=end).ToArray())
            {visible[i].gameObject.SetActive(false);Destroy(visible[i].gameObject);visible.Remove(i);}
            bool changed=false;
            for(int i=start;i<end;i++)if(!visible.ContainsKey(i))
            {
                var row=CreateRow(i,out float height);visible.Add(i,row);
                if(Math.Abs(heights[i]-height)>.5f){heights[i]=height;changed=true;}
            }
            if(changed)Recalculate();
            foreach(var pair in visible)pair.Value.anchoredPosition=new Vector2(0,-offsets[pair.Key]);
            float position=bottom?Mathf.Max(0,offsets[records.Count]-ViewportHeight):
                Mathf.Clamp(anchor<records.Count?offsets[anchor]+within:y,0,Mathf.Max(0,offsets[records.Count]-ViewportHeight));
            content.anchoredPosition=new Vector2(0,position);lastPosition=position;
        }
        RectTransform CreateRow(int i,out float height)
        {
            TalkData entry=records[i];var row=AdvWidgets.Rect("History row "+i,content,0,0,Width,216);
            int checkpoint;bool canJump=checkpoints.TryGetValue(i,out checkpoint);
            string speaker=entry.roleName=="旁白"?"":entry.roleName??"";
            AdvWidgets.Label("Speaker",row,font,speaker,238,18,TextWidth,34,24,AdvWidgets.Blue);
            var line=AdvWidgets.Label("Line",row,font,entry.content??"",238,60,TextWidth,64,28,AdvWidgets.Ink);
            line.enableWordWrapping=true;line.richText=true;line.alignment=TextAlignmentOptions.TopLeft;line.lineSpacing=7;
            float textHeight=Mathf.Max(40,line.GetPreferredValues(line.text,TextWidth,0).y+8);
            line.rectTransform.sizeDelta=new Vector2(TextWidth,textHeight);
            float buttonY=72+textHeight;
            var jump=AdvWidgets.Button(canJump?"ADV.Rollback."+checkpoint:"ADV.UnavailableRollback."+i,row,font,"",234,buttonY,46,42,()=>{if(canJump)rollback(checkpoint);});
            var icon=AdvWidgets.Rect("Return arrow",jump.transform,8,6,30,30).gameObject.AddComponent<AdvIcon>();
            icon.Symbol=13;icon.color=canJump?AdvWidgets.Ink:new Color(.47f,.43f,.36f,.55f);icon.raycastTarget=false;
            var jumpColors=jump.colors;jumpColors.normalColor=new Color(.91f,.88f,.82f,.92f);jump.colors=jumpColors;
            jump.interactable=canJump;
            if(!canJump)
            {
                var caption=jump.GetComponentInChildren<TextMeshProUGUI>();caption.color=new Color(.47f,.43f,.36f,.70f);
                var colors=jump.colors;colors.disabledColor=new Color(.92f,.90f,.86f,.40f);jump.colors=colors;
            }
            height=Mathf.Max(216,buttonY+70);row.sizeDelta=new Vector2(Width,height);
            AdvWidgets.Box("Divider",row,0,height-2,Width-8,2,new Color(.53f,.45f,.33f,.58f));
            Portrait(row,entry,canJump?history.Entries[checkpoint]:null);
            return row;
        }
        void Portrait(Transform row,TalkData data,DialogueHistory.Entry entry)
        {
            int id=data.talkRoleIds!=null && data.talkRoleIds.Count>0?data.talkRoleIds[0]:-1;
            if(id==-1)return;
            string url="role/img_unknown";bool full=false;
            if(Cfg.PersonCfgMap.TryGetValue(id,out var person))
            {
                int cloth;
                url=data.cloth!=null && data.cloth.TryGetValue(id,out cloth)?person.GetHalfIcon(cloth):person.GetHalfIcon();
                var roles=entry?.Checkpoint.State.Dialogue["roles"] as JArray;
                var role=roles?.FirstOrDefault(r=>r.Value<int>("roleId")==id);
                // Static expression variants can be replayed directly. Live2D uses
                // the native historical half portrait without creating another model.
                if(role!=null && role.Value<bool>("isIcon") && role.Value<int>("targetPose")>=0)
                {
                    string expression=RoleMgr.GetExpressionIcon(person,role.Value<int>("targetCloth"),role.Value<int>("targetPose"),entry.Checkpoint.State.Brief.GradeState);
                    if(!string.IsNullOrEmpty(expression)){url=expression;full=true;}
                }
            }
            if(string.IsNullOrEmpty(url))return;
            var mask=AdvWidgets.Rect("Portrait crop",row,18,18,180,172);mask.gameObject.AddComponent<RectMask2D>();
            var portrait=AdvWidgets.Box("Portrait",mask,0,0,180,172,Color.white);portrait.preserveAspect=true;
            var loader=new UISprite(portrait.gameObject);loader.SetTextureUrl(url);
            portrait.color=Color.clear;
            portrait.gameObject.AddComponent<AdvPortraitCrop>().Full=full;
        }
    }
    internal sealed class AdvPortraitCrop : MonoBehaviour
    {
        internal bool Full;
        sealed class Alignment {internal WeakReference Source;internal float Center;}
        static readonly Dictionary<long,Alignment> aligned=new Dictionary<long,Alignment>();
        static int sampledFrame=-1;
        internal static int Samples;
        internal static double WorstSampleMilliseconds;
        void Update()
        {
            var image=GetComponent<Image>();var sprite=image.sprite;if(sprite==null)return;
            long key=((long)sprite.GetInstanceID()<<1)+(Full?1:0);
            var size=sprite.rect.size;
            float scale=Full?180/Mathf.Max(1,size.x):Mathf.Min(180/Mathf.Max(1,size.x),172/Mathf.Max(1,size.y));
            if(!aligned.TryGetValue(key,out var metric) || !ReferenceEquals(metric.Source.Target,sprite))
            {
                // Only once per sprite and crop mode, and at most one new portrait in
                // a frame. A small alpha sample handles non-readable textures without
                // copying a full character texture or scanning pixels during scrolling.
                if(sampledFrame==Time.frameCount)return;sampledFrame=Time.frameCount;
                metric=new Alignment{Source=new WeakReference(sprite),Center=VisibleCenter(sprite,Mathf.Min(size.y,172/scale))};
                aligned[key]=metric;
            }
            float width=size.x*scale,height=size.y*scale;
            image.preserveAspect=false;
            image.rectTransform.sizeDelta=new Vector2(width,height);
            image.rectTransform.anchoredPosition=new Vector2(90-width*metric.Center,Full?0:-(172-height)*.5f);
            image.color=Color.white;enabled=false;
        }
        static float VisibleCenter(Sprite sprite,float shownHeight)
        {
            var time=System.Diagnostics.Stopwatch.StartNew();
            var previous=RenderTexture.active;RenderTexture target=null;Texture2D pixels=null;
            try
            {
                // The preview shows the upper portion of expression art. Centre the
                // visible silhouette, not transparent padding or feet below the crop.
                Rect region=sprite.textureRect;
                float height=Mathf.Min(region.height,shownHeight);
                target=RenderTexture.GetTemporary(96,96,0,RenderTextureFormat.ARGB32);
                Graphics.Blit(sprite.texture,target,new Vector2(region.width/sprite.texture.width,height/sprite.texture.height),
                    new Vector2(region.x/sprite.texture.width,(region.yMax-height)/sprite.texture.height));
                RenderTexture.active=target;
                pixels=new Texture2D(96,96,TextureFormat.RGBA32,false);
                pixels.ReadPixels(new Rect(0,0,96,96),0,0);pixels.Apply(false,false);
                var colors=pixels.GetPixels32();int left=96,right=-1;
                for(int y=0;y<96;y++)for(int x=0;x<96;x++)if(colors[y*96+x].a>=24){left=Math.Min(left,x);right=Math.Max(right,x);}
                return right>=left?(left+right+1)/192f:.5f;
            }
            catch{return .5f;}
            finally
            {
                RenderTexture.active=previous;
                if(target!=null)RenderTexture.ReleaseTemporary(target);
                if(pixels!=null)UnityEngine.Object.Destroy(pixels);
                Samples++;WorstSampleMilliseconds=Math.Max(WorstSampleMilliseconds,time.Elapsed.TotalMilliseconds);
            }
        }
    }
}
