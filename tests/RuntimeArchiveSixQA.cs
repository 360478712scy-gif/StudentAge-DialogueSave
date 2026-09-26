using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using View.Main;

public static class RuntimeArchiveSixQA
{
    static object F(string n)=>AccessTools.Field(typeof(AdvSavePage),n).GetValue(AdvSavePage.Active);
    static int N(string n)=>(int)F(n);
    static Dictionary<int,DialogueUiRecord> Slots=>(Dictionary<int,DialogueUiRecord>)F("slots");
    static bool Idle()=>AdvSavePage.Active!=null && AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1 && !(bool)F("busy") && !AdvSavePage.Active.EditorOpen && !AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null;
    static void Click(string n,Action<bool,string> check)
    {int before=AdvSkin.PlayedClicks;RuntimeUiQA.Click(n,check);check(AdvSkin.PlayedClicks==before+1,"one original click sound: "+n);}
    static IEnumerator Hover(string n,Action<bool,string> check,bool enter=true)
    {
        var target=AdvSavePage.Active.GetComponentsInChildren<Transform>().Single(t=>t.name==n).gameObject;
        var point=RuntimeUiQA.PointerReport(target)["point"];var e=new PointerEventData(EventSystem.current){position=new Vector2((float)point[0],(float)point[1])};
        var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(e,hits);
        check(hits.Count>0&&ExecuteEvents.GetEventHandler<IPointerEnterHandler>(hits[0].gameObject)==target,"hover reaches "+n);
        int before=AdvSkin.PlayedHovers;
        if(enter)ExecuteEvents.Execute(target,e,ExecuteEvents.pointerEnterHandler);else ExecuteEvents.Execute(target,e,ExecuteEvents.pointerExitHandler);
        if(enter)check(AdvSkin.PlayedHovers==before+1,"one original hover sound: "+n);
        yield return new WaitForSecondsRealtime(.22f);
    }
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/six-"+name+".png"));yield return null;}
    static IEnumerator Confirm(string key,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until)
    {yield return until(()=>AdvConfirmation.IsOpen,5,"confirmation "+key);yield return null;Click("ADV."+key+"Confirm",check);yield return until(Idle,30,"edit committed "+key);}
    static IEnumerator Tool(int id,Action<bool,string> check){Click("Archive.Tool"+id,check);yield return null;}
    internal static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _),20,"six-fix fixture ready");
        foreach(string key in new[]{"SaveEmpty","CopySave","SwapSave","Delete"})ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,key).Value=true;
        foreach(bool save in new[]{true,false})
        {
            // Bypass the ADV toolbar and use the native menu/title opening path.
            UIMgr.OpenView<SaveView>(UILayerType.Tips,null,new object[]{save});
            yield return until(Idle,20,"native save/load entry uses ADV archive "+save);
            check(N("mode")== (save?0:1),"native entry preserves save/load category");
            check(Resources.FindObjectsOfTypeAll<Canvas>().Count(c=>c.name=="DialogueSave.Archive"&&c.gameObject.activeInHierarchy)==1,"only one archive canvas per entry");
            Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&AdvSettingsTransition.Active==null,10,"native entry closes with shutter");
        }
        ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,20,"toolbar archive ready");
        var hold=(ConfigEntry<bool>)F("holdEdit");check(hold.DefaultValue is bool enabled && enabled,"keep editing defaults ON");
        if(!hold.Value){Click("Archive.HoldEdit",check);yield return null;}
        yield return Hover("Archive.HoldEdit",check);
        check(((AdvSettingsHelp)F("help")).Caption.Contains("连续编辑时请勾选"),"keep-edit explanation matches source meaning");
        yield return Shot(root,"keep-edit-help");yield return Hover("Archive.HoldEdit",check,false);
        foreach(string n in new[]{"Archive.First","Archive.UpTen","Archive.UpOne","Archive.DownOne","Archive.DownTen","Archive.Last"})
        {yield return Hover(n,check);Click(n,check);yield return null;yield return Hover(n,check,false);}
        Click("Archive.First",check);yield return null;Click("Archive.DownOne",check);yield return null;check(N("page")==2,"single down arrow advances exactly one page");
        Click("Archive.UpOne",check);yield return null;check(N("page")==1,"single up arrow returns one page");
        var rail=AdvSavePage.Active.transform.Find("Archive page rail").GetComponent<RectTransform>();
        check(1840-(rail.anchoredPosition.x+rail.rect.width)>=17,"scroll track has original right inset");
        var ticket=AdvSavePage.Active.transform.Find("Archive.Return").GetComponent<RectTransform>();var art=ticket.Find("Ticket lettering area/Archive lettering").GetComponent<RectTransform>();
        float center=ticket.InverseTransformPoint(art.TransformPoint(art.rect.center)).x;
        var image=ticket.GetComponent<Image>();float left=image.sprite.border.x/image.pixelsPerUnitMultiplier,right=image.sprite.border.z/image.pixelsPerUnitMultiplier;
        check(Mathf.Abs(center-((left+ticket.rect.width-right)/2+8))<4,"ticket lettering centered in white section");
        for(int theme=0;theme<4;theme++)
        {
            Click("Archive.Tab"+theme,check);yield return until(Idle,10,"theme ready");
            var borders=AdvSavePage.Active.GetComponentsInChildren<Image>().Where(i=>i.name=="Preview theme border").ToArray();
            check(borders.Length==12&&borders.All(i=>i.color==Color.Lerp(Color.white,AdvArchiveSkin.Themes[theme],.35f)),"every photo has theme-colored thin frame "+theme);
            check(new[]{"TIME","DATE","COMMENT"}.All(n=>AdvSavePage.Active.transform.Find(n+" tag").GetComponent<Image>().sprite!=null),"three complete textured label backgrounds");
            yield return Shot(root,"theme-"+theme);
        }
        Click("Archive.Tab0",check);yield return until(Idle,10,"save mode for operation tests");
        Click("Archive.PageNext",check);yield return null;Click("Archive.Last",check);yield return null;
        int slot=(N("page")-1)*12+1;
        Click("Archive.Slot"+slot,check);yield return Confirm("SaveEmpty",check,until);check(Slots.ContainsKey(slot),"fixture saved at empty visual slot");
        foreach(bool keep in new[]{true,false})
        {
            if(hold.Value!=keep){Click("Archive.HoldEdit",check);yield return null;}
            yield return Tool(0,check);Click("Archive.Slot"+slot,check);yield return null;Click("Archive.Slot"+(slot+1),check);yield return Confirm("CopySave",check,until);
            check(N("operation")== (keep?0:-1),"copy respects keep-edit "+keep);
            yield return Tool(1,check);Click("Archive.Slot"+(slot+1),check);yield return null;Click("Archive.Slot"+(slot+2),check);yield return Confirm("SwapSave",check,until);
            check(N("operation")== (keep?1:-1),"swap respects keep-edit "+keep);
            yield return Tool(2,check);Click("Archive.Slot"+slot,check);yield return until(()=>AdvSavePage.Active.EditorOpen,5,"note opens");yield return null;
            var input=Resources.FindObjectsOfTypeAll<TMP_InputField>().Single(i=>i.name=="Note input"&&i.gameObject.activeInHierarchy);input.text="持续编辑验证 "+keep;
            Click("Note.Apply",check);yield return until(Idle,30,"note committed");check(N("operation")== (keep?2:-1),"note respects keep-edit "+keep);
            yield return Tool(3,check);Click("Archive.Slot"+(slot+2),check);yield return Confirm("Delete",check,until);
            check(N("operation")== (keep?3:-1),"delete respects keep-edit "+keep);check(!Slots.ContainsKey(slot+2),"selected copy deleted");
            if(keep){Click("Archive.Tool3",check);yield return null;}
        }
        Click("Archive.HoldEdit",check);yield return null;check(hold.Value,"restore default keep editing ON");
        Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&AdvSettingsTransition.Active==null,10,"archive closes");
        File.WriteAllText(Path.Combine(root,"results/archive-six-success.txt"),"NATIVE_ENTRIES_TOOLBAR_LABELS_NAV_AUDIO_BORDER_KEEP_EDIT_ALL_FOUR_ON_OFF_OK");
    }
}
