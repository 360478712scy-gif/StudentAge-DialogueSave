using System;
using System.IO;
using System.Linq;
using System.Collections;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

public static class RuntimeSkinQA
{
    static Font localFontHandle;
    static byte[] localFontBytes;
    static bool LocalFontFace(Font font,int pointSize,ref UnityEngine.TextCore.LowLevel.FontEngineError __result)
    {
        if(font!=localFontHandle)return true;
        __result=UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(localFontBytes,pointSize);return false;
    }
    public static void LoadLocalFont(string root)
    {
        string path=Path.Combine(root,"PingFangSC-Regular.ttf");if(!File.Exists(path))return;
        localFontBytes=File.ReadAllBytes(path);localFontHandle=Font.CreateDynamicFontFromOSFont("Arial",64);
        // QA-only routing bypasses CrossOver's OS font substitution. Only this
        // unique handle loads local bytes; no player fonts or native files change.
        new Harmony("dialogue.qa.local-font").Patch(AccessTools.Method(typeof(UnityEngine.TextCore.LowLevel.FontEngine),"LoadFontFace",new[]{typeof(Font),typeof(int)}),prefix:new HarmonyMethod(typeof(RuntimeSkinQA),nameof(LocalFontFace)));
        var font=TMPro.TMP_FontAsset.CreateFontAsset(localFontHandle,64,8,UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,2048,2048);
        if(font==null || !font.faceInfo.familyName.Contains("PingFang"))throw new Exception("PingFang unavailable: "+font?.faceInfo.familyName);
        font.TryAddCharacters("学生时代这是一段隔离测试对白现在保存读取后应仍停在句话第二行保持清晰第三不会挡住按钮。……「」【】");
        AdvWidgets.DialogueFontOverride=font;
        File.WriteAllText(Path.Combine(root,"results/skin-font.txt"),font.faceInfo.familyName+" / "+font.faceInfo.styleName);
    }
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;
        yield return until(()=>adv.History.Entries.Count>0,35,"skin first dialogue complete");
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk.talkState==TalkState.AnimEnd,15,"typing is complete before visual capture");
        yield return new WaitForEndOfFrame(); // LatePresentation reveals the ready indicator.
        check(talk.txtex_content.fontSize==36,"normal dialogue uses 36px reference proportions");
        var plane=Resources.FindObjectsOfTypeAll<AdvPaperPlane>().Single(p=>p.gameObject.activeInHierarchy);
        check(plane.rectTransform.sizeDelta==new Vector2(30,30) && Mathf.Abs(plane.rectTransform.anchoredPosition.x-1644)<1,"hollow plane uses fixed right-hand indicator position at 30px");
        if(File.Exists(Path.Combine(root,"plane-animation-mode.txt")))
        {
            var anchor=plane.rectTransform.anchoredPosition;
            plane.gameObject.SetActive(false);plane.gameObject.SetActive(true);
            yield return until(()=>plane.AnimationTime>=.4f,3,"plane begins drawing");
            yield return Shot(root,"plane-01-drawing");
            yield return until(()=>plane.AnimationTime>=1.02f,3,"plane completes its outline");
            yield return Shot(root,"plane-02-complete");
            yield return until(()=>plane.AnimationTime>=1.6f,3,"plane scatters its lines");
            yield return Shot(root,"plane-03-scatter");
            yield return until(()=>plane.AnimationTime<.5f,3,"plane repeats from drawing phase");
            check(plane.rectTransform.anchoredPosition==anchor && plane.rectTransform.localRotation==Quaternion.identity,"animation keeps its fixed anchor without bobbing or rotation");
            check(plane.GetComponent<Canvas>()!=null,"animated strokes have a separate canvas from dialogue text");
            adv.ToggleHidden();check(!plane.gameObject.activeInHierarchy,"hiding dialogue stops the animation");adv.ToggleHidden();
            yield return null;yield return null;
            RuntimeUiQA.Click("ADV.ToolbarToggle",check);check(adv.IsFolded,"stroke canvas does not block toolbar input");
            File.WriteAllText(Path.Combine(root,"results/plane-animation-success.txt"),"DRAW_HOLD_SCATTER_REPEAT_OK");yield break;
        }
        var buttons=Resources.FindObjectsOfTypeAll<AdvSkinButton>().Where(b=>b.gameObject.activeInHierarchy).ToArray();
        check(buttons.Length==11,"all ten actions and fold use extracted artwork");
        check(buttons.All(b=>b.GetComponentInChildren<RawImage>().texture!=null),"embedded artwork decodes without external paths");
        var fold=buttons.Single(b=>b.name=="ADV.ToolbarToggle");
        check(fold.GetComponentInChildren<RawImage>().texture.name.Contains("hold_"),"toolbar uses source lock artwork");
        Vector3 center=fold.IconRect.TransformPoint(fold.IconRect.rect.center);
        yield return Shot(root,"skin-normal");
        fold.OnPointerEnter(new PointerEventData(EventSystem.current));yield return Shot(root,"skin-hover");
        check(fold.GetComponentInChildren<RawImage>().texture.name.Contains("over"),"hover selects glow texture");
        fold.OnPointerDown(new PointerEventData(EventSystem.current));yield return Shot(root,"skin-pressed");
        check(fold.GetComponentInChildren<RawImage>().texture.name.Contains("on_off"),"press selects recoloured source pressed texture");
        fold.OnPointerUp(new PointerEventData(EventSystem.current));
        fold.OnPointerExit(new PointerEventData(EventSystem.current));
        int soundCount=AdvSkin.PlayedClicks;RuntimeUiQA.Click("ADV.ToolbarToggle",check);yield return null;
        check(AdvSkin.PlayedClicks==soundCount+1,"lock click plays exactly one extracted sound on native UI channel");
        check(adv.IsFolded && Vector3.Distance(center,fold.IconRect.TransformPoint(fold.IconRect.rect.center))<.01f,"fold retains center");
        yield return Shot(root,"skin-folded");RuntimeUiQA.Click("ADV.ToolbarToggle",check);
        var logo=Resources.FindObjectsOfTypeAll<RawImage>().Single(i=>i.name=="ADV.StudentAgeLogo");
        check(logo.texture!=null && logo.uvRect.width<1 && !logo.raycastTarget,"logo is tightly bounded and cannot intercept dialogue clicks");
        var cg=AccessTools.Field(typeof(NewTalkView),"isShowingCG");bool original=(bool)cg.GetValue(talk);
        try {cg.SetValue(talk,true);yield return null;yield return null;yield return Shot(root,"skin-cg-layout");
            check(adv.IsCgPresentation,"CG switches to compact layout");}
        finally {cg.SetValue(talk,original);}
        yield return null;adv.ToggleHidden();check(adv.IsHidden && !adv.PresentationVisible,"hide removes complete skin");adv.ToggleHidden();
        yield return null;yield return null; // Canvas raycast registry updates after reactivation.
        RuntimeUiQA.Click("ADV.跳过剧情",check);yield return null;yield return Shot(root,"skin-skip-dialog");adv.CloseModal();
        adv.OpenHistory();yield return null;check(adv.ModalOpen,"original history action remains connected");adv.CloseModal();
        RuntimeUiQA.Click("ADV.保存",check);yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"artwork save action opens native save page");UIMgr.CloseView<SaveView>();
        var probe=Resources.FindObjectsOfTypeAll<AdvFirstFrameQA>().Single(p=>p.gameObject.activeInHierarchy);
        check(probe.UnshieldedFrames==0,"no old dialogue graphic flashes during skin validation");
        File.WriteAllText(Path.Combine(root,"results/skin-success.txt"),"SKIN_RUNTIME_OK; synthetic dialogue; CG layout simulated, actual CG art not exercised");
    }
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results",name+".png"));yield return new WaitForSecondsRealtime(.3f);}
}
