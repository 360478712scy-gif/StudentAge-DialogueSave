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
        const float Width=1660,ViewportHeight=784,TextWidth=1100;
        List<TalkData> records;
        DialogueHistory history;
        TMP_FontAsset font;
        Action<int> rollback;
        RectTransform content;
        ScrollRect scroll;
        Slider scrubber;
        AdvSettingsTransition openingTransition;
        internal bool Opening {get;private set;}
        float[] heights,offsets;
        float lastPosition=float.NaN;
        readonly Dictionary<int,RectTransform> visible=new Dictionary<int,RectTransform>();
        readonly Dictionary<int,int> checkpoints=new Dictionary<int,int>();
        // Cache only a bounded set of offscreen renderers, never evict history data.
        // Rows keep their original binding so an async portrait callback cannot land on another speaker.
        const int RetainedRowLimit=24;
        readonly Dictionary<int,RectTransform> retained=new Dictionary<int,RectTransform>();
        readonly LinkedList<int> retentionOrder=new LinkedList<int>();
        readonly List<int> leaving=new List<int>();
        TextMeshProUGUI dateLabel;

        void SetRecords(List<TalkData> data,DialogueHistory savedHistory)
        {
            history=savedHistory;checkpoints.Clear();
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
        }
        internal void Reload(List<TalkData> data,DialogueHistory savedHistory)
        {
            scroll.StopMovement();
            foreach(var row in visible.Values){row.gameObject.SetActive(false);Destroy(row.gameObject);}
            foreach(var row in retained.Values){row.gameObject.SetActive(false);Destroy(row.gameObject);}
            visible.Clear();retained.Clear();retentionOrder.Clear();leaving.Clear();
            SetRecords(data,savedHistory);dateLabel.text=CurrentDate();ResetLayout();
        }
        void ResetLayout()
        {
            heights=new float[records.Count];offsets=new float[records.Count+1];
            for(int i=0;i<records.Count;i++)
            {
                string text=records[i].content??"";
                int lines=1;foreach(char c in text)if(c=='\n')lines++;
                heights[i]=Mathf.Max(200,108+Mathf.Max(lines,(text.Length+32)/33)*44);
            }
            Recalculate();content.anchoredPosition=new Vector2(0,Mathf.Max(0,offsets[records.Count]-ViewportHeight));
            Refresh(true);scrubber.SetValueWithoutNotify(0);
        }

        internal void Build(Transform parent,TMP_FontAsset readingFont,List<TalkData> data,
            DialogueHistory savedHistory,Action<int> jump,Action close)
        {
            font=readingFont;rollback=jump;SetRecords(data,savedHistory);
            AdvBacklogSkin.Prepare();
            // Original backlog__bg0 center is RGBA (0,34,68,230).
            var tint=new Color32(8,39,72,230);
            var paper=AdvWidgets.Box("History fullscreen",parent,0,0,1920,1080,tint,true);
            AdvWidgets.Fill(paper.rectTransform);
            var frame=AdvWidgets.Rect("History layout",parent,0,0,1920,1080);
            frame.anchorMin=frame.anchorMax=frame.pivot=new Vector2(.5f,.5f);frame.anchoredPosition=Vector2.zero;
            var art=AdvWidgets.Rect("Campus backlog frame",frame,0,0,1920,1080).gameObject.AddComponent<Image>();
            art.sprite=AdvBacklogSkin.Sprite("backlog-frame.png");art.raycastTarget=false;
            var badge=AdvWidgets.Rect("Date badge",frame,1475,33,372,84).gameObject.AddComponent<Image>();
            badge.sprite=AdvBacklogSkin.Sprite("backlog-date.png");badge.raycastTarget=false;
            var date=AdvWidgets.Label("Game date",frame,font,CurrentDate(),1560,46,275,56,28,new Color32(231,239,248,255));
            dateLabel=date;date.alignment=TextAlignmentOptions.Center;date.fontStyle=FontStyles.Bold;
            var viewport=AdvWidgets.Rect("History viewport",frame,72,154,Width,ViewportHeight);
            viewport.gameObject.AddComponent<RectMask2D>();
            var hit=viewport.gameObject.AddComponent<Image>();hit.color=new Color(1,1,1,.001f);
            scroll=viewport.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.vertical=true;
            scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=42;
            scroll.decelerationRate=.08f;scroll.viewport=viewport;
            content=AdvWidgets.Rect("History content",viewport,0,0,Width,ViewportHeight);scroll.content=content;
            var sliderArea=AdvWidgets.Rect("History scrollbar",frame,1797,240,47,589);
            var sliderHit=sliderArea.gameObject.AddComponent<Image>();sliderHit.color=Color.clear;
            var rail=AdvSettingsSkin.Rail("Blue scrollbar rail",sliderArea,23.5f,294.5f,545,8);
            rail.rectTransform.pivot=new Vector2(.5f,.5f);rail.rectTransform.localEulerAngles=new Vector3(0,0,90);
            var handleArea=AdvWidgets.Rect("Handle area",sliderArea,1.5f,22,44,545);
            var handle=AdvSettingsSkin.Control("Paper plane scroll handle",handleArea,3,23.5f,0,44,44);
            handle.rectTransform.pivot=new Vector2(.5f,.5f);handle.raycastTarget=false;
            scrubber=sliderArea.gameObject.AddComponent<Slider>();scrubber.handleRect=handle.rectTransform;scrubber.targetGraphic=handle;
            scrubber.direction=Slider.Direction.BottomToTop;scrubber.minValue=0;scrubber.maxValue=1;
            // A vertical Slider stretches the handle across its parent width.
            // Keep zero extra width/offset so the 44px source remains circular.
            handle.rectTransform.sizeDelta=new Vector2(0,44);handle.rectTransform.anchoredPosition=Vector2.zero;
            AdvSettingsSkin.Style(scrubber,3);var nav=scrubber.navigation;nav.mode=Navigation.Mode.None;scrubber.navigation=nav;
            scrubber.onValueChanged.AddListener(value=>{scroll.StopMovement();scroll.verticalNormalizedPosition=value;Refresh(value==0);});
            scroll.onValueChanged.AddListener(value=>scrubber.SetValueWithoutNotify(value.y));
            AdvBacklogSkin.Icon("ADV.HistoryTop","top",frame,1799,156,()=>MoveTo(1));
            AdvBacklogSkin.Icon("ADV.HistoryPageUp","pageup",frame,1799,196,()=>Page(-1));
            AdvBacklogSkin.Icon("ADV.HistoryPageDown","pagedown",frame,1799,832,()=>Page(1));
            AdvBacklogSkin.Icon("ADV.HistoryBottom","end",frame,1799,872,()=>MoveTo(0));
            AdvBacklogSkin.Footer(frame,font,close);
            ResetLayout();
        }
        internal static string CurrentDate()
        {
            int round=Singleton<RoundMgr>.Ins.GetRound();
            if(!Cfg.RoundCfgMap.TryGetValue(round,out var cfg))return "DATE —";
            string season=Cfg.SeasonCfgMap.TryGetValue(cfg.season,out var seasonCfg)?seasonCfg.name:"";
            foreach(char c in "春夏秋冬")if(season.IndexOf(c)>=0){season=c.ToString();break;}
            return "DATE "+cfg.year+"-"+season;
        }
        internal void AnimateOpen(){StartCoroutine(Open());}
        System.Collections.IEnumerator Open()
        {
            Opening=true;var group=GetComponent<CanvasGroup>()??gameObject.AddComponent<CanvasGroup>();group.alpha=0;
            yield return new WaitForEndOfFrame();
            var capture=AdvSettingsTransition.Capture();group.alpha=1;
            AdvSettingsTransition.Play(capture,true,()=>{if(this!=null)Opening=false;},sortingOrder:31200);
            openingTransition=AdvSettingsTransition.Active;
        }
        void OnDestroy(){if(openingTransition!=null && AdvSettingsTransition.Active==openingTransition)Destroy(openingTransition.gameObject);}
        void MoveTo(float value){scroll.StopMovement();scroll.verticalNormalizedPosition=value;Refresh(value==0);scrubber.SetValueWithoutNotify(value);}
        void Page(int direction){scroll.StopMovement();content.anchoredPosition+=new Vector2(0,direction*ViewportHeight*.85f);Refresh(false);}
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
            leaving.Clear();foreach(int i in visible.Keys)if(i<start || i>=end)leaving.Add(i);
            foreach(int i in leaving)
            {var row=visible[i];row.gameObject.SetActive(false);visible.Remove(i);retained.Add(i,row);retentionOrder.AddLast(i);}
            bool changed=false;
            for(int i=start;i<end;i++)if(!visible.ContainsKey(i))
            {
                float height;
                if(retained.TryGetValue(i,out var row))
                {retained.Remove(i);retentionOrder.Remove(i);row.gameObject.SetActive(true);height=row.sizeDelta.y;}
                else row=CreateRow(i,out height);
                visible.Add(i,row);
                if(Math.Abs(heights[i]-height)>.5f){heights[i]=height;changed=true;}
            }
            while(retained.Count>RetainedRowLimit)
            {int oldest=retentionOrder.First.Value;retentionOrder.RemoveFirst();Destroy(retained[oldest].gameObject);retained.Remove(oldest);}
            if(changed)Recalculate();
            foreach(var pair in visible)pair.Value.anchoredPosition=new Vector2(0,-offsets[pair.Key]);
            float position=bottom?Mathf.Max(0,offsets[records.Count]-ViewportHeight):
                Mathf.Clamp(anchor<records.Count?offsets[anchor]+within:y,0,Mathf.Max(0,offsets[records.Count]-ViewportHeight));
            content.anchoredPosition=new Vector2(0,position);lastPosition=position;
        }
        RectTransform CreateRow(int i,out float height)
        {
            TalkData entry=records[i];var row=AdvWidgets.Rect("History row "+i,content,0,0,Width,200);
            int checkpoint;bool canJump=checkpoints.TryGetValue(i,out checkpoint);
            string speaker=entry.roleName=="旁白"?"":entry.roleName??"";
            AdvWidgets.Label("Speaker",row,font,speaker,552,10,TextWidth,32,24,new Color32(140,200,230,255));
            float textY=string.IsNullOrEmpty(speaker)?18:48;
            var line=AdvWidgets.Label("Line",row,font,entry.content??"",552,textY,TextWidth,64,34,new Color32(235,235,242,255));
            line.fontSharedMaterial=AdvWidgets.LightOutline(line.font);
            line.enableWordWrapping=true;line.richText=true;line.alignment=TextAlignmentOptions.TopLeft;line.lineSpacing=7;
            float textHeight=Mathf.Max(40,line.GetPreferredValues(line.text,TextWidth,0).y+8);
            line.rectTransform.sizeDelta=new Vector2(TextWidth,textHeight);
            float buttonY=textY+textHeight+12;
            var jump=AdvBacklogSkin.Icon(canJump?"ADV.Rollback."+checkpoint:"ADV.UnavailableRollback."+i,"jump",row,356,buttonY,()=>{if(canJump)rollback(checkpoint);});
            jump.interactable=canJump;if(!canJump)jump.targetGraphic.color=new Color(1,1,1,.4f);
            height=Mathf.Max(200,buttonY+66);row.sizeDelta=new Vector2(Width,height);
            AdvWidgets.Box("Divider",row,0,height-2,Width,2,new Color(0,.045f,.1f,.62f));
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
            var mask=AdvWidgets.Rect("Portrait crop",row,140,18,180,172);mask.gameObject.AddComponent<RectMask2D>();
            var portrait=AdvWidgets.Box("Portrait",mask,0,0,180,172,Color.white);portrait.preserveAspect=true;
            var loader=new UISprite(portrait.gameObject);loader.SetTextureUrl(url);
            portrait.color=Color.clear;
            portrait.gameObject.AddComponent<AdvPortraitCrop>().Full=full;
        }
    }
    internal sealed class AdvPortraitCrop : MonoBehaviour
    {
        internal bool Full;
        sealed class Alignment {internal WeakReference Source;internal float Center;internal bool Pending,Fallback;}
        static readonly Dictionary<long,Alignment> aligned=new Dictionary<long,Alignment>();
        static int sampledFrame=-1;
        internal static int Samples;
        internal static double WorstSampleMilliseconds;
        void Update()
        {
            if(AdvSettingsTransition.Active!=null)return;
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
                metric=new Alignment{Source=new WeakReference(sprite),Center=.5f};aligned[key]=metric;
                SampleCenter(sprite,Mathf.Min(size.y,172/scale),metric);
            }
            if(metric.Pending)return;
            if(metric.Fallback)
            {
                if(sampledFrame==Time.frameCount)return;sampledFrame=Time.frameCount;
                metric.Center=VisibleCenter(sprite,Mathf.Min(size.y,172/scale));metric.Fallback=false;
            }
            float width=size.x*scale,height=size.y*scale;
            image.preserveAspect=false;
            image.rectTransform.sizeDelta=new Vector2(width,height);
            image.rectTransform.anchoredPosition=new Vector2(90-width*metric.Center,Full?0:-(172-height)*.5f);
            image.color=Color.white;enabled=false;
        }
        static void SampleCenter(Sprite sprite,float shownHeight,Alignment metric)
        {
            if(!SystemInfo.supportsAsyncGPUReadback){metric.Center=VisibleCenter(sprite,shownHeight);return;}
            var target=RenderTexture.GetTemporary(96,96,0,RenderTextureFormat.ARGB32);
            try
            {
                Rect region=sprite.textureRect;float height=Mathf.Min(region.height,shownHeight);
                Graphics.Blit(sprite.texture,target,new Vector2(region.width/sprite.texture.width,height/sprite.texture.height),
                    new Vector2(region.x/sprite.texture.width,(region.yMax-height)/sprite.texture.height));
                metric.Pending=true;
                UnityEngine.Rendering.AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32,request=>{
                    try
                    {
                        metric.Fallback=request.hasError;
                        if(!request.hasError)
                        {
                            var pixels=request.GetData<Color32>();int left=96,right=-1;
                            for(int y=0;y<96;y++)for(int x=0;x<96;x++)if(pixels[y*96+x].a>=24){left=Math.Min(left,x);right=Math.Max(right,x);}
                            metric.Center=right>=left?(left+right+1)/192f:.5f;
                        }
                    }
                    finally {metric.Pending=false;Samples++;RenderTexture.ReleaseTemporary(target);}
                });
            }
            catch {metric.Pending=false;RenderTexture.ReleaseTemporary(target);metric.Center=VisibleCenter(sprite,shownHeight);}
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
