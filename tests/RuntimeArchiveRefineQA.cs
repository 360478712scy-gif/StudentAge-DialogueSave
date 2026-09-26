using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using View.Main;

public static class RuntimeArchiveRefineQA
{
    static object Field(string name)=>AccessTools.Field(typeof(AdvSavePage),name).GetValue(AdvSavePage.Active);
    static Dictionary<int,DialogueUiRecord> Slots=>(Dictionary<int,DialogueUiRecord>)Field("slots");
    static bool Idle()=>AdvSavePage.Active!=null && AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1 && !AdvSavePage.Active.EditorOpen && AdvSettingsTransition.Active==null && !AdvConfirmation.IsOpen && !(bool)Field("busy");
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/archive-refine-"+name+".png"));yield return null;}
    static void Click(string name,Action<bool,string> check)=>RuntimeUiQA.Click(name,check);
    static IEnumerator Hover(string name,Action<bool,string> check,bool enter=true)
    {
        var target=AdvSavePage.Active.GetComponentsInChildren<Transform>().Single(t=>t.name==name).gameObject;
        var report=RuntimeUiQA.PointerReport(target);var point=report["point"];
        var e=new PointerEventData(EventSystem.current){position=new Vector2((float)point[0],(float)point[1])};
        var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(e,hits);
        check(hits.Count>0 && ExecuteEvents.GetEventHandler<IPointerEnterHandler>(hits[0].gameObject)==target,"hover raycast reaches "+name);
        if(enter)ExecuteEvents.Execute(target,e,ExecuteEvents.pointerEnterHandler);else ExecuteEvents.Execute(target,e,ExecuteEvents.pointerExitHandler);
        yield return new WaitForSecondsRealtime(.25f);
    }
    internal static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        if(File.Exists(Path.Combine(root,"archive-edit-regression.txt")))
        {yield return RuntimeArchiveQA.Run(adapter,ui,service,check,until,root);File.WriteAllText(Path.Combine(root,"results/archive-edit-regression-success.txt"),"ARCHIVE_RAIL_POINTER_SAVE_EDIT_LOAD_OK");yield break;}
        yield return until(()=>adapter.CanCapture(out _),20,"refinement fixture ready");
        var role=Singleton<RoleMgr>.Ins.GetRole();var property=AccessTools.Property(role.GetType(),"Sex");var original=role.Sex;
        var nativeFiles=new Dictionary<GenderDefine,string>();
        try
        {
            int i=0;
            foreach(var gender in new[]{GenderDefine.Male,GenderDefine.Female})
            {
                property.SetValue(role,gender,null);
                int slot=++i;UiResult result=null;
                ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,20,"gender fixture save page ready");
                check(service.BeginMenu(true,out _),"begin capture after archive is visible");
                var old=service.ArchiveList(DialogueUiCategory.Manual).FirstOrDefault(r=>r.RunId==service.ArchiveRun&&r.Slot==slot);
                service.Save(DialogueUiCategory.Manual,slot,old?.RevisionId,r=>{if(!r.IsPending)result=r;});
                yield return until(()=>result!=null,35,"dialogue gender fixture saved");check(result.Success,"dialogue gender fixture committed "+gender+": "+result.Message);
                Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null && AdvSettingsTransition.Active==null,10,"fixture page closes");
                bool done=false,ok=false;
                Game.ManualSaveGame(900+slot,"Archive gender QA",null,value=>{done=true;ok=value;});
                yield return until(()=>done,30,"native gender fixture saved");check(ok,"native gender fixture committed "+gender);
                nativeFiles[gender]=SaveMgr.GetPref("LatestSaveKey","");
            }
        }
        finally{property.SetValue(role,original,null);service.EndMenu();}
        ui.Open(false);yield return until(Idle,20,"refined archive opens");
        check(!AdvSavePage.Active.GetComponentsInChildren<Button>().Any(b=>b.name=="Archive.Title"),"archive has no return-to-title action");
        check(AdvSavePage.Active.transform.Find("Archive header tint")==null,"generated background has no flat tint overlay");
        check(AdvSavePage.Active.transform.Find("Archive.Run")==null,"gender switch removed");
        check(AdvSavePage.Active.transform.Find("Status")==null,"no unframed status text");
        var paper=AdvSavePage.Active.transform.Find("Archive paper").GetComponent<Image>();
        check(paper.sprite!=null && paper.sprite.rect.width==1760,"original gradient rounded panel loaded");
        var rail=AdvSavePage.Active.transform.Find("Archive page rail");
        check(rail.Find("Blue gradient track").GetComponent<Image>().sprite!=null,"generated gradient track reused");
        check(rail.Find("Handle area/Paper plane thumb").GetComponent<RectTransform>().rect.width>=50,"paper plane thumb is at least original 50px size");
        int initialPages=(int)Field("pageCount");
        Click("Archive.PageNext",check);yield return null;
        check((int)Field("pageCount")==initialPages+1 && (int)Field("page")==1,"plus appends a page without navigating");
        Click("Archive.Tab0",check);yield return until(Idle,10,"save mode opens for shared page check");
        check((int)Field("pageCount")==initialPages+1,"save and load share total pages");
        Click("Archive.PageBack",check);yield return null;
        check((int)Field("pageCount")==initialPages,"minus removes only final empty page");
        Click("Archive.Tab1",check);yield return until(Idle,10,"load mode shares removal");
        check((int)Field("pageCount")==initialPages,"load sees page removal");
        var footer=AdvSavePage.Active.transform.Find("Archive.Return").GetComponent<RectTransform>();
        check(footer.rect.width==440,"return ticket widened to 440px");
        var caption=footer.GetComponentsInChildren<Image>().Single(x=>x.name=="Archive lettering").rectTransform;
        var corners=new Vector3[4];caption.GetWorldCorners(corners);
        check(corners.All(p=>{var local=footer.InverseTransformPoint(p);return local.x>80&&local.x<footer.rect.width-18&&local.y<0&&local.y>-footer.rect.height;}),"ticket lettering stays inside white area with padding");
        var toggle=AdvSavePage.Active.transform.Find("Archive.Detail");
        check(toggle.GetComponent<Image>().sprite==null&&toggle.GetComponent<Image>().color.a==0&&toggle.GetComponentInChildren<AdvConfirmToggle>()!=null,"detail checkbox is a plain circle and label without capsule");
        var backgrounds=new HashSet<int>();
        for(int mode=0;mode<4;mode++)
        {
            Click("Archive.Tab"+mode,check);yield return until(Idle,10,"generated theme opens "+mode);
            backgrounds.Add(AdvSavePage.Active.transform.Find("Campus archive background").GetComponent<Image>().sprite.texture.GetInstanceID());
            check(AdvSavePage.Active.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Archive.Slot"))==12,"reference grid keeps four columns and three rows");
            yield return Shot(root,"theme-"+mode);
        }
        check(backgrounds.Count==4,"four themes use four separate painted background textures");
        foreach(int mode in new[]{1,3})
        {
            Click("Archive.Tab"+mode,check);yield return until(Idle,10,"gender preview category opens");
            var seen=new Dictionary<GenderDefine,string>();
            foreach(var gender in new[]{GenderDefine.Male,GenderDefine.Female})
            {
                yield return until(()=>Slots.Values.Any(r=>r.Gender==(int)gender),20,"saved gender records ready");
                check(Slots.Values.Any(r=>r.Gender==(int)GenderDefine.Male)&&Slots.Values.Any(r=>r.Gender==(int)GenderDefine.Female),"both genders remain accessible without a switch in category "+mode);
                var pair=Slots.First(p=>mode==3?p.Value.RevisionId==nativeFiles[gender]:p.Value.Slot==((gender==GenderDefine.Male)?1:2)&&p.Value.RunId==service.ArchiveRun);
                AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{(pair.Key-1)/12+1});yield return null;
                Func<Transform> card=()=>AdvSavePage.Active.transform.Find("Archive cards/Archive.Slot"+pair.Key);
                yield return until(()=>card()?.Find("Speaker chibi")?.GetComponent<Image>().sprite!=null && card()?.Find("Saved background")?.GetComponent<Image>().sprite!=null,20,"chibi and background load "+mode+" "+gender);
                seen[gender]=(string)AccessTools.Field(typeof(ArchivePreview),"current").GetValue(card().Find("Speaker chibi").GetComponent<ArchivePreview>());
                check(card().Find("Speaker chibi").GetComponent<Image>().color.a==1,"chibi is visibly opaque");
                if(mode==3)
                {
                    check((int)AccessTools.Method(typeof(AdvSavePage),"PreviewBackground").Invoke(AdvSavePage.Active,new object[]{pair.Value})==Cfg.MapCfgMap[1].bg,"native preview is protagonist home");
                    var expectedUrl=Cfg.PersonCfgMap[0].GetComicIcon(false,true,gender);Sprite expected=null;
                    AtlasMgr.GetSpriteAsync(expectedUrl,s=>expected=s);yield return until(()=>expected!=null,10,"gender protagonist atlas exists");
                    var shown=card().Find("Speaker chibi").GetComponent<Image>().sprite;
                    File.AppendAllText(Path.Combine(root,"results/archive-chibi-sources.txt"),gender+" url="+seen[gender]+" expected="+expectedUrl+" displayed="+shown.rect+" expectedRect="+expected.rect+"\n");
                    check(expectedUrl==seen[gender] && shown.rect==expected.rect && shown.textureRect==expected.textureRect,"native chibi matches saved protagonist gender "+gender);
                }
                AccessTools.Method(typeof(AdvSavePage),"ShowDetail").Invoke(AdvSavePage.Active,new object[]{pair.Key,pair.Value});
                yield return Hover("Archive.Slot"+pair.Key,check);
                var help=(AdvSettingsHelp)Field("help");check(help.Opacity==1 && help.Caption.Contains("读取"),"read hint appears inside animated help strip");
                var helpRect=help.GetComponent<RectTransform>();var ticket=AdvSavePage.Active.transform.Find("Archive.Return").GetComponent<RectTransform>();
                check(helpRect.anchoredPosition.x+helpRect.rect.width<ticket.anchoredPosition.x,"help and wide return ticket do not overlap");
                yield return Shot(root,"category-"+mode+"-"+gender);
                yield return Hover("Archive.Slot"+pair.Key,check,false);check(help.Opacity==0,"hover hint fades out on exit");
            }
            if(mode==3)
            {
                check(seen[GenderDefine.Male]!=seen[GenderDefine.Female],"male and female native saves display distinct protagonist art");
                int occupiedPages=(int)Field("pageCount");
                Click("Archive.PageBack",check);yield return null;
                check((int)Field("pageCount")==occupiedPages,"minus cannot remove occupied last page");
                check(((AdvSettingsHelp)Field("help")).Caption.Contains("仍有存档"),"occupied-page explanation uses help strip");

            }
        }
        Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null && AdvSettingsTransition.Active==null,10,"refined archive exit animation completes");
        File.WriteAllText(Path.Combine(root,"results/archive-refine-visual-success.txt"),"ARCHIVE_SOURCE_PANEL_HINT_RAIL_WIDE_TICKET_SHARED_PAGES_AND_BOTH_GENDERS_OK");
        yield return RuntimeArchiveQA.Run(adapter,ui,service,check,until,root);
        File.WriteAllText(Path.Combine(root,"results/archive-refine-success.txt"),"ARCHIVE_REFINEMENT_VISUALS_AND_SAVE_EDIT_LOAD_REGRESSION_OK");
    }
}
