using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Config;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using View.Main;

namespace StudentAgeDialogueSave.UI
{
    internal sealed class AdvSavePage:MonoBehaviour,IScrollHandler
    {
        internal static AdvSavePage Active;
        internal const int SlotsPerPage=12;
        float lastWheel=-1;
        SaveView view;DialogueSaveService service;TMP_FontAsset font;
        readonly List<GameObject> originals=new List<GameObject>();
        readonly Button[] tabs=new Button[4],tools=new Button[4];
        readonly Dictionary<int,DialogueUiRecord> slots=new Dictionary<int,DialogueUiRecord>();
        readonly List<GameObject> cards=new List<GameObject>();
        NativeArchiveBrowser native;GameObject body,editor;Image detail,preview,title,background;TextMeshProUGUI number,time,date,comment,pagination;
        AdvConfirmToggle detailCheck,holdCheck;
        BepInEx.Configuration.ConfigEntry<bool> holdEdit;
        readonly Dictionary<int,int> nativePositions=new Dictionary<int,int>(),nativeDisplayPositions=new Dictionary<int,int>();
        readonly Dictionary<string,int> dialogueDisplayPositions=new Dictionary<string,int>();
        CanvasGroup group;AdvSettingsHelp help;GameObject emptyDetail;Slider pageRail;ScrollRect commentScroll;Button detailToggle;
        int mode,page=1,pageCount=10,operation=-1,sourceSlot,hoverSlot;DialogueUiRecord source;
        bool nativeAutomatic,detailed,busy,closing,dirty,updatingRail,titleBrowse;
        BepInEx.Configuration.ConfigEntry<int>[] pageLimits;
        string Status{set{help.SetStatus(value);}}
        internal static AdvSavePage Open(SaveView view,DialogueSaveService service)
        {
            var root=AdvWidgets.Canvas("DialogueSave.Archive",30700);AdvWidgets.CenterDesign(root);var page=root.AddComponent<AdvSavePage>();Active=page;
            page.view=view;page.service=service;page.titleBrowse=DialogueUiController.IsTitleScreen();page.mode=view.isSaveMode&&!page.titleBrowse?0:1;page.font=AdvWidgets.ReadingFont(TMP_Settings.defaultFontAsset);
            var config=AdvDialogueController.Active.Configuration;
            page.holdEdit=config.Bind("Archive","KeepEditing",true,"编辑完成后保持当前编辑工具；关闭则成功一次后取消选择。");
            page.pageLimits=new[]{config.Bind("Archive","ManualPages",10,"保存与读取共用的存档页数。"),config.Bind("Archive","QuickPages",1,"快速存档显示页数。"),config.Bind("Archive","NativePages",10,"原版存档显示页数。")};
            page.native=new NativeArchiveBrowser();page.native.Changed=()=>page.dirty=true;
            foreach(Transform t in view.transform)if(t.gameObject.activeSelf){page.originals.Add(t.gameObject);t.gameObject.SetActive(false);}
            page.group=root.AddComponent<CanvasGroup>();page.group.alpha=0;page.Build();page.RefreshRecords();page.StartCoroutine(page.OpenAnimation());return page;
        }
        IEnumerator OpenAnimation()
        {
            yield return new WaitForEndOfFrame();var snapshot=AdvSettingsTransition.Capture();group.alpha=1;
            Canvas.ForceUpdateCanvases();AdvSettingsTransition.Play(snapshot,true,sortingOrder:32500);
        }
        void Build()
        {
            background=AdvWidgets.Rect("Campus archive background",transform,0,0,1920,1080).gameObject.AddComponent<Image>();background.sprite=AdvArchiveSkin.Background(mode);background.raycastTarget=true;
            var shadow=AdvWidgets.Rect("Archive paper soft shadow",transform,80,122,1760,840).gameObject.AddComponent<ArchivePaperShadow>();shadow.raycastTarget=false;
            var paper=AdvWidgets.Rect("Archive paper",transform,80,120,1760,840).gameObject.AddComponent<Image>();paper.sprite=AdvArchiveSkin.Paper();
            title=AdvArchiveSkin.Letter(mode,transform,44,24,340,72);title.color=Color.white;
            var titleEdge=title.gameObject.AddComponent<Outline>();titleEdge.effectColor=AdvArchiveSkin.Ink;titleEdge.effectDistance=new Vector2(2,-2);
            string[] captions={"保存","读取","快速读取","原版存档"};
            for(int i=0;i<4;i++){int id=i;tabs[i]=AdvArchiveSkin.Button("Archive.Tab"+i,transform,font,captions[i],1122+i*178,25,166,66,()=>Switch(id),1);}
            var detailHit=AdvWidgets.Box("Archive.Detail",transform,155,138,215,44,Color.clear,true);
            detailToggle=detailHit.gameObject.AddComponent<AdvButton>();detailToggle.targetGraphic=detailHit;detailToggle.transition=Selectable.Transition.None;
            detailCheck=detailHit.gameObject.AddComponent<AdvConfirmToggle>();
            detailCheck.Icon=AdvWidgets.Rect("Checked",detailHit.transform,0,8,28,28).gameObject.AddComponent<Image>();detailCheck.Icon.raycastTarget=false;
            detailCheck.Label=AdvWidgets.Label("Caption",detailHit.transform,font,"详细显示注释",34,0,180,44,22,AdvArchiveSkin.Ink);detailCheck.Refresh();
            detailToggle.onClick.AddListener(()=>{detailed=!detailed;RefreshCards();});
            var holdHit=AdvWidgets.Box("Archive.HoldEdit",transform,385,138,255,44,Color.clear,true);
            var holdButton=holdHit.gameObject.AddComponent<AdvButton>();holdButton.targetGraphic=holdHit;holdButton.transition=Selectable.Transition.None;
            holdCheck=holdHit.gameObject.AddComponent<AdvConfirmToggle>();holdCheck.Selected=holdEdit.Value;
            holdCheck.Icon=AdvWidgets.Rect("Checked",holdHit.transform,0,8,28,28).gameObject.AddComponent<Image>();holdCheck.Icon.raycastTarget=false;
            holdCheck.Label=AdvWidgets.Label("Caption",holdHit.transform,font,"保持编辑模式",34,0,218,44,22,AdvArchiveSkin.Ink);holdCheck.Refresh();
            holdButton.onClick.AddListener(()=>{holdEdit.Value=!holdEdit.Value;holdEdit.ConfigFile.Save();holdCheck.Selected=holdEdit.Value;holdCheck.Refresh();});
            string[] operations={"复制存档","交换存档","编辑备注","删除存档"};
            for(int i=0;i<4;i++){int id=i;tools[i]=AdvArchiveSkin.Button("Archive.Tool"+i,transform,font,operations[i],660+i*230,138,212,46,()=>SelectOperation(id));}
            pagination=Label("Pages","",1630,140,146,42,22);pagination.alignment=TextAlignmentOptions.Center;
            var removePage=AdvArchiveSkin.PageButton("Archive.PageBack",false,transform,1585,140,()=>ResizePages(-1));
            var addPage=AdvArchiveSkin.PageButton("Archive.PageNext",true,transform,1776,140,()=>ResizePages(1));
            AdvArchiveSkin.NavigationButton("Archive.First","top",transform,1775,207,()=>SetPage(1));
            AdvArchiveSkin.NavigationButton("Archive.UpTen","up10",transform,1775,246,()=>TurnPage(-10));
            AdvArchiveSkin.NavigationButton("Archive.UpOne","up",transform,1775,286,()=>TurnPage(-1));
            AdvArchiveSkin.NavigationButton("Archive.DownOne","dw",transform,1775,814,()=>TurnPage(1));
            AdvArchiveSkin.NavigationButton("Archive.DownTen","dw10",transform,1775,855,()=>TurnPage(10));
            AdvArchiveSkin.NavigationButton("Archive.Last","btm",transform,1775,895,()=>SetPage(pageCount));
            var rail=AdvWidgets.Rect("Archive page rail",transform,1769,328,54,486);
            var track=AdvSettingsSkin.Rail("Blue gradient track",rail,27,243,434,8);track.rectTransform.pivot=new Vector2(.5f,.5f);track.rectTransform.localEulerAngles=new Vector3(0,0,90);
            var railHit=rail.gameObject.AddComponent<Image>();railHit.color=Color.clear;
            var handleArea=AdvWidgets.Rect("Handle area",rail,0,26,54,434);
            var handle=AdvSettingsSkin.Control("Paper plane thumb",handleArea,3,0,0,54,54);
            handle.rectTransform.pivot=new Vector2(.5f,.5f);
            pageRail=rail.gameObject.AddComponent<Slider>();pageRail.direction=Slider.Direction.BottomToTop;pageRail.wholeNumbers=true;
            pageRail.handleRect=handle.rectTransform;pageRail.targetGraphic=handle;
            handle.rectTransform.sizeDelta=new Vector2(0,54);handle.rectTransform.anchoredPosition=Vector2.zero;AdvSettingsSkin.Style(pageRail,3);pageRail.onValueChanged.AddListener(v=>{if(!updatingRail)SetPage(pageCount+1-(int)v);});
            rail.gameObject.AddComponent<ArchiveRailAudio>();
            detail=AdvWidgets.Rect("Detail frame",transform,90,194,560,756).gameObject.AddComponent<Image>();detail.raycastTarget=false;
            // Left preview uses the original 540x304 photo and source field positions.
            emptyDetail=AdvWidgets.Rect("Empty detail campus label",transform,100,240,540,304).gameObject;
            NoImage(emptyDetail.transform,0,0,540,304,true);
            preview=Picture("Detail photograph",transform,100,240,540,304);
            number=Label("Detail number","",410,202,220,35,24);number.alignment=TextAlignmentOptions.Right;
            var labelY=new[]{622,684,746};string[] names={"TIME","DATE","COMMENT"};
            for(int i=0;i<3;i++)
            {var tag=AdvWidgets.Rect(names[i]+" tag",transform,112,labelY[i]-2,132,38).gameObject.AddComponent<Image>();tag.sprite=AdvArchiveSkin.InformationTag();tag.raycastTarget=false;var l=Label(names[i],names[i],116,labelY[i],124,34,17);l.color=AdvArchiveSkin.Themes[mode];l.alignment=TextAlignmentOptions.Center;l.fontStyle=FontStyles.Bold;}
            time=Label("Saved time","",252,618,380,46,24);date=Label("Story date","",252,680,380,46,27);date.fontStyle=FontStyles.Bold;
            comment=Label("Comment","",252,744,380,190,27);comment.enableWordWrapping=true;comment.alignment=TextAlignmentOptions.TopLeft;comment.richText=false;
            var commentViewport=AdvWidgets.Rect("Detail comment viewport",transform,252,744,380,190);commentViewport.gameObject.AddComponent<RectMask2D>();
            var commentHit=commentViewport.gameObject.AddComponent<Image>();commentHit.color=Color.clear;
            comment.rectTransform.SetParent(commentViewport,false);comment.rectTransform.anchoredPosition=Vector2.zero;
            commentScroll=commentViewport.gameObject.AddComponent<ScrollRect>();commentScroll.viewport=commentViewport;commentScroll.content=comment.rectTransform;commentScroll.horizontal=false;commentScroll.vertical=true;commentScroll.scrollSensitivity=28;
            body=AdvWidgets.Rect("Archive cards",transform,654,194,1104,756).gameObject;
            help=AdvSettingsHelp.Create(transform,font,1160);
            Hint(holdButton.gameObject,"设置数据编辑后是否保持编辑模式状态。\n连续编辑时请勾选。");
            Hint(removePage.gameObject,"从最后减少一页；有存档的末页不会删除。保存与读取共用页数。");
            Hint(addPage.gameObject,"在最后增加一页。保存与读取共用页数。");
            foreach(string name in new[]{"Archive.First","Archive.UpTen","Archive.UpOne","Archive.DownOne","Archive.DownTen","Archive.Last"})Hint(transform.Find(name).gameObject,"切换当前显示的存档页；上方加减号用于增减总页数。");
            foreach(var b in tabs)Hint(b.gameObject,"切换存档分类和颜色；空位保存，有数据的位置读取。");
            for(int i=0;i<tools.Length;i++)Hint(tools[i].gameObject,new[]{"选择来源，再选目标复制；再次点击取消。","选择两个位置交换；可交换到空位。","编辑备注后，存档只显示备注，不再显示原对白。","选择要删除的存档；删除前会再次确认。"}[i]);
            Hint(detailToggle.gameObject,"切换存档卡片的图片预览和详细注释。");
            Footer("Archive.Return",5,1400,()=>view.CloseView());
        }
        void Hint(GameObject go,string caption){var target=go.AddComponent<SettingsHelpTarget>();target.Help=help;target.Description=caption;}
        TextMeshProUGUI Label(string name,string text,float x,float y,float w,float h,float size)=>AdvWidgets.Label(name,transform,font,text,x,y,w,h,size,AdvArchiveSkin.Ink);
        Image Picture(string name,Transform parent,float x,float y,float w,float h)
        {var i=AdvWidgets.Rect(name,parent,x,y,w,h).gameObject.AddComponent<Image>();i.preserveAspect=true;i.raycastTarget=false;i.color=Color.clear;return i;}
        void Footer(string name,int lettering,float x,Action action)
        {
            var image=AdvSettingsSkin.Control(name,transform,2,x,980,440,78);var b=image.gameObject.AddComponent<AdvButton>();b.targetGraphic=image;AdvSettingsSkin.Style(b,2);
            float left=image.sprite.border.x/image.pixelsPerUnitMultiplier,right=image.sprite.border.z/image.pixelsPerUnitMultiplier;
            float whiteWidth=440-left-right;
            var area=AdvWidgets.Rect("Ticket lettering area",image.transform,left,15,whiteWidth,48);area.gameObject.AddComponent<RectMask2D>();
            var art=AdvArchiveSkin.Letter(lettering,area,(whiteWidth-260)/2+8,7,260,34);var ink=b.gameObject.AddComponent<SettingsButtonInk>();ink.Text=art;ink.SetSelected(false);b.onClick.AddListener(()=>{if(!busy)action();});
        }
        void Switch(int id)
        {
            if(id==mode || (id==0 && titleBrowse) || busy || editor!=null || AdvSettingsTransition.Active!=null)return;
            var snapshot=AdvSettingsTransition.Capture();mode=id;page=1;hoverSlot=0;operation=-1;source=null;RefreshRecords();
            AdvSettingsTransition.Play(snapshot,true,sortingOrder:32500);
        }
        int LimitIndex=>mode==3?2:mode==2?1:0;
        void ResizePages(int delta)
        {
            if(busy || editor!=null || AdvSettingsTransition.Active!=null)return;
            int desired=pageCount+delta;
            if(desired<1){Status="至少保留一页存档位。";return;}
            if(delta<0 && slots.Keys.Any(slot=>slot>desired*SlotsPerPage)){Status="最后一页仍有存档，请先移动存档后再减少页数。";return;}
            pageLimits[LimitIndex].Value=desired;pageLimits[LimitIndex].ConfigFile.Save();
            page=Mathf.Min(page,desired);RefreshRecords();Status=delta>0?"已在最后增加一页存档位。":"已移除最后一页空存档位。";
        }
        static string RecordKey(DialogueUiRecord record)=>record.Category+"/"+record.RunId+"/"+record.Slot;
        int NativeTarget(int slot)
        {
            if(nativePositions.TryGetValue(slot,out int actual))return actual;
            int candidate=slot;while(native.Slots.ContainsKey(candidate))candidate++;return candidate;
        }
        internal void RefreshRecords()
        {
            if(closing)return;slots.Clear();nativePositions.Clear();
            if(mode==3)
            {
                native.Refresh();
                // Manual cards sit at their stable native number, so overwrite/delete never
                // reshuffles other saves. Automatic saves are listed in order.
                foreach(var pair in native.Slots.Where(p=>nativeAutomatic?p.Value.isAuto:p.Value.isManual).OrderBy(p=>p.Key))
                {
                    int slot=nativeAutomatic?slots.Count+1:pair.Key;
                    slots[slot]=native.Record(pair.Value);nativePositions[slot]=pair.Key;
                }
                native.RequestGenderHeaders();
            }
            else
            {
                var list=service.ArchiveList(mode==2?DialogueUiCategory.Quick:DialogueUiCategory.Manual);
                foreach(var r in list.OrderBy(r=>r.RunId==service.ArchiveRun?0:1).ThenBy(r=>r.RunId).ThenBy(r=>r.CreatedUtc))
                {
                    int slot=dialogueDisplayPositions.TryGetValue(RecordKey(r),out int mapped)?mapped:Math.Max(1,r.Slot);
                    while(slots.ContainsKey(slot))slot++;slots[slot]=r;
                }
            }
            pageCount=Math.Max(Math.Max(1,pageLimits[LimitIndex].Value),(slots.Keys.DefaultIfEmpty(0).Max()+SlotsPerPage-1)/SlotsPerPage);page=Mathf.Clamp(page,1,pageCount);
            RefreshCards();
        }
        void RefreshCards()
        {
            foreach(var go in cards){go.SetActive(false);Destroy(go);}cards.Clear();
            for(int i=0;i<4;i++)AdvArchiveSkin.Select(tabs[i],mode==i,1);
            tabs[0].interactable=!titleBrowse;
            if(titleBrowse)
            {
                var tint=tabs[0].GetComponent<SettingsButtonInk>();tint.Text.color=new Color32(140,150,157,255);
                ((Image)tabs[0].targetGraphic).color=new Color(.7f,.7f,.7f,1);
                if(tint.Brush!=null)tint.Brush.enabled=false;
            }
            for(int i=0;i<4;i++)AdvArchiveSkin.Select(tools[i],mode==3 && i==0?nativeAutomatic:operation==i);
            tools[0].GetComponentInChildren<TextMeshProUGUI>().text=mode==3?"自动存档":"复制存档";
            tools[0].GetComponent<SettingsHelpTarget>().Description=mode==3?"开启显示原版自动存档，关闭只显示手动存档；可同时使用备注或删除工具。":"选择来源，再选目标复制；再次点击取消。";
            tools[1].GetComponentInChildren<TextMeshProUGUI>().text=mode==3?"原版界面":"交换存档";
            tools[1].GetComponent<SettingsHelpTarget>().Description=mode==3?"打开游戏原版存档界面。":"选择两个位置交换；可交换到空位。";
            title.sprite=AdvArchiveSkin.LetterSprite(mode);background.sprite=AdvArchiveSkin.Background(mode);
            updatingRail=true;pageRail.maxValue=pageCount;pageRail.minValue=1;pageRail.SetValueWithoutNotify(pageCount+1-page);updatingRail=false;
            emptyDetail.GetComponentInChildren<Image>().color=AdvArchiveSkin.DetailColor(mode);
            detail.sprite=AdvArchiveSkin.Art("detail_base__null_",mode);pagination.text=page.ToString("0000")+"/"+pageCount.ToString("0000");
            foreach(string label in new[]{"TIME","DATE","COMMENT"})transform.Find(label).GetComponent<TextMeshProUGUI>().color=AdvArchiveSkin.Themes[mode];
            detailCheck.Selected=detailed;detailCheck.Refresh();
            for(int i=0;i<SlotsPerPage;i++){int slot=(page-1)*SlotsPerPage+i+1;if(mode==3 && nativePositions.TryGetValue(slot,out int actual))native.Request(actual);BuildSlot(i,slot);}
            slots.TryGetValue(hoverSlot,out var hovered);ShowDetail(hoverSlot,hovered);dirty=false;
        }
        static string SlotName(int slot)=>"s"+((slot-1)/SlotsPerPage+1).ToString("0000")+"-"+((slot-1)%SlotsPerPage+1).ToString("00");
        static string StoryDate(DialogueUiRecord r)
        {if(r==null)return "";string season=r.SeasonLabel??"";foreach(char c in "春夏秋冬")if(season.Contains(c.ToString())){season=c.ToString();break;}return (r.YearLabel??"").Replace("年","")+"-"+season;}
        string Caption(DialogueUiRecord r)
        {
            if(r==null)return "";
            if(r.Comment!=null)return r.Comment;
            if(mode!=3)return r.Summary??"";
            string Safe(string text)=>(text??"").Replace("<","＜").Replace(">","＞");
            return Safe(r.RoleName)+" · "+Safe(r.Location)+" · <color=#"+ColorUtility.ToHtmlStringRGB(SeasonColor(r))+">"+Safe(StoryDate(r).Replace("-",""))+"</color>";
        }
        static Color SeasonColor(DialogueUiRecord r)
        {
            string season=r?.SeasonLabel??"";
            return season.Contains("春")?new Color32(47,128,82,255):season.Contains("夏")?new Color32(35,119,156,255):season.Contains("秋")?new Color32(160,94,30,255):season.Contains("冬")?new Color32(106,89,160,255):AdvArchiveSkin.Ink;
        }
        bool HasPreview(DialogueUiRecord r)=>r!=null && (!string.IsNullOrEmpty(r.PreviewImageUrl) || Cfg.BgCfgMap.ContainsKey(PreviewBackground(r)));
        void BuildSlot(int index,int slot)
        {
            slots.TryGetValue(slot,out var record);var root=AdvWidgets.Rect("Archive.Slot"+slot,body.transform,(index%4)*276,(index/4)*252,276,252);cards.Add(root.gameObject);
            var image=root.gameObject.AddComponent<Image>();image.sprite=AdvArchiveSkin.Art("item_off",mode);
            var button=root.gameObject.AddComponent<AdvButton>();button.targetGraphic=image;button.transition=Selectable.Transition.None;var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
            var id=AdvWidgets.Label("Slot id",root,font,SlotName(slot),24,11,126,28,19,AdvArchiveSkin.Ink);id.fontStyle=FontStyles.Bold;
            var era=AdvWidgets.Label("Date",root,font,StoryDate(record),134,11,125,28,17,AdvArchiveSkin.Ink);era.alignment=TextAlignmentOptions.Right;era.fontStyle=FontStyles.Bold;if(mode==3)era.color=SeasonColor(record);
            if(!detailed)
            {
                // The preview overlaps the solid header: leave that part untouched.
                // Integer, symmetric one-unit sides and bottom avoid half-pixel widths.
                AdvWidgets.Box("Preview theme border",root,27,46,222,119,Color.Lerp(Color.white,AdvArchiveSkin.Themes[mode],.35f));
                var photo=Picture("Saved background",root,28,40,220,124);SetPreview(photo,record);
                if(!HasPreview(record))NoImage(root,28,40,220,124);
                if(record!=null){var chibi=Picture("Speaker chibi",root,192,100,72,70);SetChibi(chibi,record);}
            }
            var text=AdvWidgets.Label("Comment",root,font,Caption(record),12,detailed?48:168,254,detailed?172:52,22,AdvArchiveSkin.Ink);text.enableWordWrapping=true;text.alignment=TextAlignmentOptions.TopLeft;text.overflowMode=TextOverflowModes.Ellipsis;text.richText=mode==3 && record?.Comment==null;
            var stamp=AdvWidgets.Label("Time",root,font,record==null?"":record.CreatedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),106,222,159,22,14,new Color32(126,150,166,255));stamp.alignment=TextAlignmentOptions.Right;
            var cursor=AdvWidgets.Rect("Hover outline",root,2,2,272,248).gameObject.AddComponent<Image>();cursor.sprite=AdvArchiveSkin.Cursor(mode);cursor.enabled=false;cursor.raycastTarget=false;
            var hover=root.gameObject.AddComponent<ArchiveSlotHover>();hover.Cursor=cursor;hover.Hover=over=>{if(over)ShowDetail(slot,record);};
            hover.Background=image;hover.Idle=AdvArchiveSkin.Art("item_off",mode);hover.Over=AdvArchiveSkin.Art("item_over",mode);hover.Down=AdvArchiveSkin.Art("item_on",mode);hover.Selected=sourceSlot==slot && source!=null;
            Hint(root.gameObject,IsSavePage?(record==null?"保存到 ":"覆盖 ")+SlotName(slot)+"。":record==null?"此存档位为空，无法读取。":"读取 "+SlotName(slot)+" 的数据。");
            button.onClick.AddListener(()=>ClickSlot(slot,record));
        }
        void NoImage(Transform parent,float x,float y,float w,float h,bool colored=false)
        {
            AdvWidgets.Box("No image paper",parent,x,y,w,h,colored?AdvArchiveSkin.DetailColor(mode):Color.white);
            var label=AdvWidgets.Label("No image",parent,font,"NO IMAGE",x,y+h*.25f,w,h*.45f,h*.18f,colored?AdvArchiveSkin.Ink:AdvArchiveSkin.Themes[mode]);label.alignment=TextAlignmentOptions.Center;label.fontStyle=FontStyles.Bold;
            var sub=AdvWidgets.Label("Student Age",parent,font,"Student Age",x,y+h*.68f,w,h*.2f,h*.1f,colored?AdvArchiveSkin.Ink:AdvArchiveSkin.Themes[mode]);sub.alignment=TextAlignmentOptions.Center;sub.fontStyle=FontStyles.Italic;
        }
        void SetPreview(Image image,DialogueUiRecord record)
        {
            ArchivePreview.Set(image,null);
            if(!string.IsNullOrEmpty(record?.PreviewImageUrl)){image.color=Color.white;ArchivePreview.Set(image,record.PreviewImageUrl);return;}
            if(record==null || !Cfg.BgCfgMap.TryGetValue(PreviewBackground(record),out var background))return;
            image.color=Color.white;ArchivePreview.Set(image,background.GetBgUrl(record.GradeState));
        }
        int PreviewBackground(DialogueUiRecord record)=>record?.BackgroundId>0?record.BackgroundId:mode==3?(Cfg.MapCfgMap.TryGetValue(1,out var home)?home.bg:100):0;
        void SetChibi(Image image,DialogueUiRecord record)
        {
            var gender=(GenderDefine)record.Gender;Cfg.PersonCfgMap.TryGetValue(mode==3?0:record.SpeakerId,out var person);
            if(mode!=3 && (person==null || person.id==0) && !string.IsNullOrEmpty(record.Speaker))
            {
                string speaker=System.Text.RegularExpressions.Regex.Replace(record.Speaker,"<[^>]*>","").Trim('【','】',' ',':','：');
                var named=Cfg.PersonCfgMap.Values.FirstOrDefault(p=>p.id>0 && p.name==speaker);if(named!=null)person=named;
            }
            string url=person?.GetComicIcon(true,true,gender);
            if(string.IsNullOrEmpty(url) && Cfg.PersonCfgMap.TryGetValue(0,out var protagonist))url=protagonist.GetComicIcon(false,true,gender);
            Cfg.PersonCfgMap.TryGetValue(0,out var fallback);
            ArchivePreview.Set(image,url,person?.GetComicIcon(true,false,gender),atlas:true,lastFallback:fallback?.GetComicIcon(false,true,gender));
        }
        void ShowDetail(int slot,DialogueUiRecord record)
        {
            if(slot>0)hoverSlot=slot;number.text=slot==0?"":SlotName(slot);time.text=record==null?"":record.CreatedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");date.text=StoryDate(record);comment.richText=mode==3 && record?.Comment==null;comment.text=Caption(record);date.color=mode==3?SeasonColor(record):AdvArchiveSkin.Ink;
            comment.rectTransform.sizeDelta=new Vector2(380,Mathf.Max(190,comment.GetPreferredValues(comment.text,380,0).y));commentScroll.verticalNormalizedPosition=1;
            emptyDetail.SetActive(!HasPreview(record));SetPreview(preview,record);
            Status=operation>=0?(source==null?"选择要"+new[]{"复制","交换","编辑备注","删除"}[operation]+"的存档。":"已选择 "+SlotName(sourceSlot)+"，请选择目标存档位；再次点工具可取消。"):
                "";
            if(record?.IsConflict==true)Status="此存档存在设备分支，可选择读取；编辑前请先处理冲突。";
        }
        void MarkSource(){foreach(var card in cards){var h=card.GetComponent<ArchiveSlotHover>();h.Selected=source!=null && card.name=="Archive.Slot"+sourceSlot;h.Refresh();}}
        void SelectOperation(int id){if(busy || editor!=null || AdvSettingsTransition.Active!=null)return;if(mode==3 && id==0){nativeAutomatic=!nativeAutomatic;page=1;hoverSlot=0;source=null;RefreshRecords();return;}if(mode==3 && id==1){DialogueUiController.OpenOriginalArchive(view);return;}operation=operation==id?-1:id;source=null;RefreshCards();}
        void SetPage(int next){if(busy || editor!=null || closing)return;next=Mathf.Clamp(next,1,pageCount);if(page==next)return;page=next;hoverSlot=0;RefreshCards();}
        void TurnPage(int delta)=>SetPage(page+delta);
        public void OnScroll(PointerEventData e)
        {
            if(busy || closing || editor!=null || AdvConfirmation.IsOpen || AdvSettingsTransition.Active!=null || Mathf.Abs(e.scrollDelta.y)<.01f)return;
            // Wheel/trackpad magnitude never produces partial rows or skips many pages.
            if(Time.unscaledTime-lastWheel<.16f)return;
            lastWheel=Time.unscaledTime;TurnPage(e.scrollDelta.y<0?1:-1);e.Use();
        }
        bool IsSavePage=>mode==0 || (mode==3 && view.isSaveMode && !nativeAutomatic);
        void ClickSlot(int slot,DialogueUiRecord record)
        {
            if(busy || closing || editor!=null || AdvConfirmation.IsOpen || AdvSettingsTransition.Active!=null)return;
            if(operation<0)
            {
                if(IsSavePage)
                {
                    if(titleBrowse){Status="请先进入游戏后保存。";return;}
                    if(mode==3 && nativeAutomatic){Status="自动存档由游戏生成；关闭自动存档可查看或创建手动存档。";return;}
                    if(mode==3 && Game.GetGameState()!=GameState.Running){Status="请先进入游戏后保存。";return;}
                    if(!service.IsDialogueContext && mode!=3){Status="请在对话中保存。";return;}
                    AdvConfirmation.Show(font,record==null?"SaveEmpty":"Overwrite",(record==null?"保存到 ":"覆盖 ")+SlotName(slot)+" 吗？",()=>Save(slot,record));
                }
                else if(record==null)Status="此存档位为空，无法读取。";
                else AdvConfirmation.Show(font,mode==3?"NativeLoad":"Load","读取 "+SlotName(slot)+"，离开当前进度吗？",()=>Load(record));
                return;
            }
            if(operation<2)
            {
                if(source==null){if(record==null)return;source=record;sourceSlot=slot;RefreshCards();ShowDetail(slot,record);return;}
                if(sourceSlot==slot){source=null;MarkSource();ShowDetail(slot,record);return;}
                int op=operation,from=sourceSlot;var selected=source;
                AdvConfirmation.Show(font,op==0?"CopySave":"SwapSave",(op==0?"复制 ":"交换 ")+SlotName(from)+" → "+SlotName(slot)+" 吗？",()=>Edit(op,from,selected,slot,record,null));return;
            }
            if(record==null)return;
            if(operation==2)EditNote(slot,record);else AdvConfirmation.Show(font,"Delete","删除 "+SlotName(slot)+" 的存档吗？",()=>Edit(3,slot,record,slot,record,null));
        }
        void Save(int slot,DialogueUiRecord replaced=null)
        {
            busy=true;
            if(mode==3)
            {
                int position=native.FreeNativePosition(),target=NativeTarget(slot);
                Game.ManualSaveGame(position,null,null,async success=>
                {
                    if(this==null || closing)return;
                    if(!success){busy=false;Status="原版保存未完成。";return;}
                    try{native.Refresh();var saved=native.Slots.FirstOrDefault(p=>p.Value.isManual && p.Value.pos==position);if(saved.Value==null)throw new InvalidOperationException("找不到新写入的原版存档，请刷新后检查。");if(saved.Key!=target){await native.Edit("swap",saved.Key,target);if(replaced!=null)await native.Edit("delete",saved.Key,saved.Key,null);}nativeDisplayPositions[target]=slot;if(this==null || closing)return;RefreshRecords();Status="原版存档已保存。";}
                    catch(Exception ex){if(this!=null && !closing)Status="存档已保存，但排列更新失败："+ex.Message;}
                    finally{busy=false;}
                });
            }
            else
            {
                var category=mode==2?DialogueUiCategory.Quick:DialogueUiCategory.Manual;string currentRun=service.ArchiveRun;
                var used=service.ArchiveList(category).Where(r=>r.RunId==currentRun).Select(r=>r.Slot).ToHashSet();
                int logical=replaced!=null && replaced.RunId==currentRun?replaced.Slot:slot;
                if(replaced==null || replaced.RunId!=currentRun)while(used.Contains(logical))logical++;
                service.ArchiveSave(logical,category,currentRun,result=>{if(result.Success){if(replaced!=null)dialogueDisplayPositions.Remove(RecordKey(replaced));dialogueDisplayPositions[category+"/"+currentRun+"/"+logical]=slot;}Result(result);},replaced?.RevisionId);
            }
        }
        void Load(DialogueUiRecord record)
        {
            if(!record.CanLoad){Status=record.StatusReason;return;}busy=true;
            if(mode==3){view.CloseView();Game.LoadGame(record.RevisionId);return;}
            service.Load(record.RevisionId,result=>{Result(result);if(result.Success)view.CloseView();});
        }
        async void Edit(int op,int from,DialogueUiRecord selected,int target,DialogueUiRecord destination,string note)
        {
            busy=true;string verb=new[]{"copy","swap","note","delete"}[op];
            if(mode==3){try{int actualFrom=nativePositions[from],actualTarget=NativeTarget(target);if(!native.Slots.TryGetValue(actualFrom,out var current) || current.fileName!=selected.RevisionId || (destination!=null && (!native.Slots.TryGetValue(actualTarget,out var currentTarget) || currentTarget.fileName!=destination.RevisionId)))throw new InvalidOperationException("存档列表已变化，请重新选择。");await native.Edit(verb,actualFrom,actualTarget,note);if(op<2){nativeDisplayPositions[actualTarget]=target;if(destination!=null && op==1)nativeDisplayPositions[actualFrom]=from;}Result(new UiResult(true,"操作完成。"));}catch(Exception ex){Result(new UiResult(false,ex.Message));}return;}
            int logical=destination?.Slot??target;
            if(destination==null && op<2)
            {
                var used=service.ArchiveList(selected.Category).Where(r=>r.RunId==selected.RunId).Select(r=>r.Slot).ToHashSet();
                while(used.Contains(logical))logical++;
            }
            if(op==3)service.Delete(selected.RevisionId,Result);else
            {
                int logicalTarget=logical;
                service.EditArchive(verb,selected,logical,destination,note,result=>{if(result.Success && op<2)dialogueDisplayPositions[selected.Category+"/"+selected.RunId+"/"+logicalTarget]=target;Result(result);});
            }
        }
        void Result(UiResult result)
        {
            if(this==null || closing)return;if(result.IsPending){Status=result.Message;return;}
            busy=false;source=null;if(result.Success){if(operation>=0 && !holdEdit.Value)operation=-1;RefreshRecords();}Status=result.Message;
        }
        void EditNote(int slot,DialogueUiRecord record)
        {
            var opening=AdvSettingsTransition.Capture();
            editor=AdvWidgets.Canvas("Archive.NoteEditor",32100);AdvWidgets.CenterDesign(editor);AdvWidgets.Box("Shade",editor.transform,0,0,1920,1080,new Color(0,0,0,.4f),true);
            var panel=AdvSettingsSkin.Card("备注",editor.transform,450,280,1020,440).rectTransform;
            var caption=AdvWidgets.Label("Title",panel,font,"编辑备注",32,18,950,46,30,AdvArchiveSkin.Ink);
            var area=AdvWidgets.Rect("Note input",panel,34,88,952,230);var paper=area.gameObject.AddComponent<Image>();paper.color=Color.white;area.gameObject.AddComponent<RectMask2D>();
            var text=AdvWidgets.Label("Text",area,font,"",14,10,924,210,28,AdvArchiveSkin.Ink);text.alignment=TextAlignmentOptions.TopLeft;text.enableWordWrapping=true;text.richText=false;
            // TMP creates its caret during OnEnable only when textComponent is already bound.
            area.gameObject.SetActive(false);
            var input=area.gameObject.AddComponent<TMP_InputField>();input.textComponent=text;input.textViewport=area;input.lineType=TMP_InputField.LineType.MultiLineNewline;input.characterLimit=4096;input.text=record.Comment??"";
            input.targetGraphic=paper;input.customCaretColor=true;input.caretColor=AdvArchiveSkin.Ink;input.caretWidth=3;input.caretBlinkRate=.85f;input.selectionColor=new Color(.27f,.68f,.9f,.35f);area.gameObject.SetActive(true);
            AdvArchiveSkin.Button("Note.Cancel",panel,font,"取消",112,348,340,54,CloseEditor);
            AdvArchiveSkin.Button("Note.Apply",panel,font,"确定",562,348,340,54,()=>{string value=input.text;CloseEditor();Edit(2,slot,record,slot,record,value);});AdvSettingsTransition.Play(opening,true,()=>{if(input!=null && editor!=null){input.Select();input.ActivateInputField();}},sortingOrder:32500,horizontal:true);
        }
        internal void RequestClose(){if(busy || AdvSettingsTransition.Active!=null)return;if(editor!=null)CloseEditor();else view.CloseView();}
        internal bool EditorOpen=>editor!=null;
        internal void CloseEditor(){if(editor==null || AdvSettingsTransition.Active!=null)return;var old=editor;editor=null;AdvSettingsTransition.Exit(()=>{old.SetActive(false);Destroy(old);},horizontal:true);}
        void Update(){if(mode==3 && !closing)native.RequestGenderHeaders();if(dirty && !busy && editor==null)RefreshRecords();}
        internal void Close()
        {
            if(closing)return;closing=true;native.Changed=null;
            if(editor!=null){Destroy(editor);editor=null;}
            foreach(var original in originals)if(original!=null)original.SetActive(true);originals.Clear();service.EndMenu();if(Active==this)Active=null;gameObject.SetActive(false);Destroy(gameObject);
        }
        void OnDestroy(){if(!closing)Close();}
    }
}
