using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

public static class RuntimeCgQA
{
    static TextMeshProUGUI Body(NewTalkView talk)=>(TextMeshProUGUI)AccessTools.Method(typeof(NewTalkView),"GetTalkTxt").Invoke(talk,null);
    static object Field(AdvDialogueController adv,string name)=>AccessTools.Field(typeof(AdvDialogueController),name).GetValue(adv);
    static void Speaker(NewTalkView talk,TalkCfg cfg,List<int> ids,string name)
    {cfg.roleIds=ids;cfg.roleName=name;AccessTools.Method(typeof(NewTalkView),"RefreshTalkingRole",new[]{typeof(List<int>),typeof(string)}).Invoke(talk,new object[]{ids,name});}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk.talkState==TalkState.AnimEnd,25,"CG fixture starts at completed native dialogue");
        var cfg=(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk);
        var oldIds=cfg.roleIds;string oldName=cfg.roleName;bool oldWatermark=adv.Preferences.Watermark.Value;
        var tags=Cfg.TalkCfgMap.Values.SelectMany(c=>System.Text.RegularExpressions.Regex.Matches(c.content??"","<[^>]+>").Cast<System.Text.RegularExpressions.Match>().Select(m=>m.Value))
            .GroupBy(t=>t).OrderByDescending(g=>g.Count()).Select(g=>g.Count()+"\t"+g.Key);
        File.WriteAllLines(Path.Combine(root,"results/native-dialogue-markup.txt"),tags);
        const string line="这是 CG 模式中的第一行对白。\n第二行保留清晰的阅读空间。\n第三行不会挤到底部操作按钮。";
        try
        {
            adv.Preferences.Watermark.Value=true;adv.ApplyPreferences();
            Speaker(talk,cfg,new List<int>{0},"林川");talk.DoText("放学后的风吹过走廊。");
            yield return until(()=>talk.talkState==TalkState.AnimEnd,15,"normal spoken line completes");
            yield return null;
            var veil=(AdvVeil)Field(adv,"veil");var tone=veil.color;var texture=veil.texture;
            var logo=(RawImage)Field(adv,"watermark");var name=(TextMeshProUGUI)Field(adv,"nameLabel");var rule=(RectTransform)Field(adv,"nameRule");
            var normalFont=Body(talk).font;Body(talk).ForceMeshUpdate(false,true);
            check(logo.gameObject.activeInHierarchy && name.text=="【林川】" && Body(talk).GetParsedText().StartsWith("「"),"normal dialogue retains watermark and its brackets");
            check((name.fontStyle&FontStyles.Bold)!=0,"normal speaker is bold");
            var toolbar=Resources.FindObjectsOfTypeAll<AdvSkinButton>().Where(b=>b.gameObject.activeInHierarchy).OrderBy(b=>b.name).ToArray();
            check(toolbar.Length==11,"normal toolbar uses ten current actions and fold artwork");
            yield return TextEffects(talk,check,until,root,"normal");
            bool opened=false;int cgId=Cfg.CGCfgMap.Keys.First(id=>id>0);
            AccessTools.Method(typeof(NewTalkView),"ShowCG").Invoke(talk,new object[]{cgId,(Action)(()=>{CaptureNativeCg(talk,root);opened=true;Speaker(talk,cfg,new List<int>{0},"林川");talk.DoText(line);})});
            yield return until(()=>opened && adv.IsCgPresentation && talk.talkState==TalkState.AnimEnd,35,"real native CG opens and typing completes");
            yield return null;yield return null;
            var text=Body(talk);text.ForceMeshUpdate(false,true);
            check(text.font==normalFont && text.font.faceInfo.familyName=="Microsoft YaHei","CG shares the verified Chinese font with normal dialogue");
            check(text.text==line && text.GetParsedText()==line,"CG adds no quotation marks and preserves raw dialogue");
            check(name.text=="【林川】" && name.alignment==TextAlignmentOptions.Center,"CG speaker has requested brackets and is center aligned");
            check((name.fontStyle&FontStyles.Bold)!=0,"CG speaker is bold");
            check(!((AdvPaper)Field(adv,"namePaper")).gameObject.activeSelf,"CG has no nameplate background");
            check(Mathf.Abs(name.rectTransform.anchoredPosition.x+name.rectTransform.sizeDelta.x/2-960)<.1f,"CG name is centered on screen");
            check(rule.gameObject.activeInHierarchy && Mathf.Abs(rule.anchoredPosition.x+rule.sizeDelta.x/2-960)<.1f,"CG underline is visible and centered");
            check(Mathf.Abs(-rule.anchoredPosition.y-(-name.rectTransform.anchoredPosition.y+name.rectTransform.sizeDelta.y)-4)<.1f && Mathf.Abs(-text.rectTransform.anchoredPosition.y-(-rule.anchoredPosition.y+rule.sizeDelta.y)-4)<.1f,"CG name separator and body use compact four-pixel gaps without overlap");
            check(rule.sizeDelta.y==4 && rule.GetComponent<Shadow>()!=null && rule.Find("Separator highlight 0")!=null && rule.Find("Separator highlight 1")!=null && Mathf.Abs(rule.sizeDelta.x-(name.rectTransform.sizeDelta.x+name.fontSize*2))<.1f,"CG gray highlight separator has dark rim shadow and two extra character widths");
            check(!logo.gameObject.activeSelf,"CG hides watermark even when preference is on");
            adv.ApplyPreferences();check(!logo.gameObject.activeSelf,"applying settings in CG does not reveal watermark");
            var cgVeil=(RawImage)Field(adv,"cgVeil");var native=(Image)Field(adv,"cgVeilSource");
            check(!veil.gameObject.activeSelf && cgVeil.gameObject.activeInHierarchy && cgVeil.color.r==0 && cgVeil.color.g==0 && cgVeil.color.b==0,"CG uses the native black plate instead of normal dialogue plate");
            check(ReadPixels(native.mainTexture).Select(p=>p.a).SequenceEqual(ReadPixels(cgVeil.texture).Select(p=>p.a)),"CG original mask alpha is unchanged at every pixel");
            var originalCorners=new Vector3[4];var currentCorners=new Vector3[4];native.rectTransform.GetWorldCorners(originalCorners);cgVeil.rectTransform.GetWorldCorners(currentCorners);
            check(Enumerable.Range(0,4).All(i=>Vector2.Distance(RectTransformUtility.WorldToScreenPoint(native.canvas.worldCamera,originalCorners[i]),RectTransformUtility.WorldToScreenPoint(null,currentCorners[i]))<.5f),"CG original mask screen position width and height are preserved");
            var cgPlateSize=cgVeil.rectTransform.sizeDelta;var cgPlatePos=cgVeil.rectTransform.anchoredPosition;
            check(text.fontSize==38 && text.textInfo.lineCount==3,"CG retains larger centered three-line layout");
            check(toolbar.All(b=>b.gameObject.activeInHierarchy && b.GetComponentInChildren<RawImage>().texture!=null),"CG keeps the same current toolbar objects and assets");
            yield return Shot(root,"cg-speaker-current-ui");
            talk.DoText("这是两行对白的第一行。\n第二行保留原有阅读空间。");
            yield return null;yield return null;
            var twoLineSize=cgVeil.rectTransform.sizeDelta;var twoLinePos=cgVeil.rectTransform.anchoredPosition;
            check(twoLineSize.y<cgPlateSize.y && twoLineSize.y>cgPlateSize.y*.5f && twoLinePos.y<cgPlatePos.y,"two-line CG panel is lower than three-line panel");
            yield return until(()=>talk.talkState==TalkState.AnimEnd,15,"two-line CG completes");
            check(cgVeil.rectTransform.sizeDelta==twoLineSize && cgVeil.rectTransform.anchoredPosition==twoLinePos,"CG panel height is stable throughout typewriter");
            yield return Shot(root,"cg-two-line-current-ui");
            yield return TextEffects(talk,check,until,root,"cg");
            RuntimeUiQA.Click("ADV.ToolbarToggle",check);check(adv.IsFolded,"current CG toolbar handles native pointer click");
            RuntimeUiQA.Click("ADV.ToolbarToggle",check);check(!adv.IsFolded,"current CG toolbar unfolds without advancing dialogue");
            Speaker(talk,cfg,new List<int>{-1},null);talk.DoText("蓝色花海里，风轻轻吹过。");
            yield return until(()=>talk.talkState==TalkState.AnimEnd,15,"CG narration completes");yield return null;
            check(!name.gameObject.activeSelf && !rule.gameObject.activeSelf && !Body(talk).GetParsedText().StartsWith("「"),"CG narration has neither speaker rule nor added quotes");
            check(cgVeil.rectTransform.sizeDelta.y<twoLineSize.y && Mathf.Abs(cgVeil.rectTransform.sizeDelta.y-cgPlateSize.y*.5f)<1 && cgVeil.rectTransform.anchoredPosition.y<twoLinePos.y,"one-line CG panel is lowest at half the three-line height");
            yield return Shot(root,"cg-narration-current-ui");
            Speaker(talk,cfg,new List<int>{0},"林川");
            AccessTools.Method(typeof(NewTalkView),"HideCGComic").Invoke(talk,null);talk.DoText("回到教室，继续刚才的对话。");
            yield return until(()=>!adv.IsCgPresentation && talk.talkState==TalkState.AnimEnd,20,"leaving CG returns to normal dialogue");yield return null;
            Body(talk).ForceMeshUpdate(false,true);
            check(name.text=="【林川】" && Body(talk).GetParsedText().StartsWith("「") && Body(talk).GetParsedText().EndsWith("」"),"same speaker regains normal brackets after CG");
            check(logo.gameObject.activeInHierarchy && !rule.gameObject.activeSelf && veil.gameObject.activeInHierarchy && !cgVeil.gameObject.activeSelf && veil.color==tone && veil.texture==texture,"normal watermark and plate return while CG rule and plate disappear");
            yield return Shot(root,"cg-return-normal");
            File.WriteAllText(Path.Combine(root,"results/cg-visual-success.txt"),"CG_BOLD_NAME_GRAY_RULE_BLACK_NATIVE_MASK_ADAPTIVE_HEIGHT_RICH_TEXT_RESTORATION_OK");
        }
        finally{Speaker(talk,cfg,oldIds,oldName);adv.Preferences.Watermark.Value=oldWatermark;adv.ApplyPreferences();}
    }
    static Color32[] ReadPixels(Texture source)
    {
        var previous=RenderTexture.active;var rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32);
        var copy=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);
        try{Graphics.Blit(source,rt);RenderTexture.active=rt;copy.ReadPixels(new Rect(0,0,source.width,source.height),0,0);copy.Apply();return copy.GetPixels32();}
        finally{RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);UnityEngine.Object.Destroy(copy);}
    }
    static void CaptureNativeCg(NewTalkView talk,string root)
    {
        var group=(GameObject)AccessTools.Method(typeof(NewTalkView),"GetTalkGroup").Invoke(talk,null);
        var rect=group.GetComponent<RectTransform>();var corners=new Vector3[4];rect.GetWorldCorners(corners);
        var records=new List<string>{"rect="+rect.rect+" size="+rect.sizeDelta+" pos="+rect.anchoredPosition+" anchors="+rect.anchorMin+"/"+rect.anchorMax,
            "world="+string.Join(";",corners.Select(c=>c.ToString())),"components="+string.Join(",",group.GetComponents<Component>().Select(c=>c.GetType().FullName))};
        foreach(var g in group.GetComponents<Graphic>())records.Add("graphic="+g.GetType().FullName+" color="+g.color+" material="+g.material.name+" texture="+g.mainTexture.name+"/"+g.mainTexture.width+"x"+g.mainTexture.height);
        foreach(var i in group.GetComponents<Image>())records.Add("image="+i.sprite?.name+" rect="+i.sprite?.rect+" type="+i.type);
        File.WriteAllLines(Path.Combine(root,"results/native-cg-backplate.txt"),records);
    }
    static IEnumerator TextEffects(NewTalkView talk,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root,string mode)
    {
        const string effects="基准 <size=54>大字</size> <size=150%>放大</size> <size=70%>小字</size> <color=#FF4040>红色</color> <color=#40FF80>绿色<b>粗体</b></color>\n<i>斜体</i> <u>下划线</u> <s>删除线</s> <alpha=#80>半透<alpha=#FF> 恢复 <sup>上标</sup><sub>下标</sub>，逐字显示之后点击补全。";
        int index=talk.tmpTalkIdx;string old=talk.tmpTalks[index];
        try
        {
            talk.tmpTalks[index]=effects;talk.DoText(effects);
            var text=Body(talk);
            yield return until(()=>text.text.Length>0 && talk.talkState==TalkState.Anim,10,mode+" native typewriter produces partial rich text");
            text.ForceMeshUpdate(false,true);
            check(text.richText && !text.overrideColorTags,mode+" preserves native rich-text and author color parsing");
            check(!text.GetParsedText().Contains("<size") && !text.GetParsedText().Contains("<color"),mode+" partial typing does not display markup");
            yield return until(()=>(int)AccessTools.Field(typeof(NewTalkView),"waitFrame").GetValue(talk)==0,5,mode+" native click guard clears");
            talk.OnClickNext();yield return null;text.ForceMeshUpdate(false,true);
            check(talk.talkState==TalkState.AnimEnd && text.text==effects,mode+" native click completes exact original rich text");
            var chars=text.textInfo.characterInfo.Take(text.textInfo.characterCount).ToArray();
            Func<char,TMP_CharacterInfo> glyph=c=>chars.First(v=>v.character==c);
            float baseline=glyph('基').pointSize;
            check(Mathf.Abs(glyph('大').pointSize-54)<.1f && Mathf.Abs(glyph('放').pointSize/baseline-1.5f)<.01f && Mathf.Abs(glyph('小').pointSize/baseline-.7f)<.01f,mode+" absolute and relative sizes render on actual glyphs");
            var red=glyph('红').color;var green=glyph('绿').color;var bold=glyph('粗');
            check(red.r>red.g*2 && green.g>green.r*2 && bold.color.g>bold.color.r*2,mode+" red green and nested color tags survive white base text");
            check((bold.style&FontStyles.Bold)!=0 && (glyph('斜').style&FontStyles.Italic)!=0 && (glyph('下').style&FontStyles.Underline)!=0 && (glyph('删').style&FontStyles.Strikethrough)!=0,mode+" bold italic underline and strike styles render");
            check(glyph('半').color.a<160 && glyph('恢').color.a>240,mode+" alpha tag and reset render");
            File.WriteAllLines(Path.Combine(root,"results",mode+"-glyph-effects.txt"),chars.Select(c=>c.character+" size="+c.pointSize+" scale="+c.scale+" baseline="+c.baseLine+" style="+c.style+" color="+c.color));
            check(glyph('上').scale<glyph('恢').scale && glyph('上').baseLine>glyph('恢').baseLine,mode+" superscript uses smaller elevated glyphs");
            check(chars.Any(c=>c.character=='下' && (c.style&FontStyles.Subscript)!=0 && c.scale<glyph('恢').scale),mode+" subscript is retained");
            check(!text.GetParsedText().Contains("<") && !text.GetParsedText().Contains(">"),mode+" completed effects do not expose markup");
            yield return Shot(root,mode+"-rich-text-effects");
        }
        finally{talk.tmpTalks[index]=old;}
    }
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results",name+".png"));yield return new WaitForSecondsRealtime(.25f);}
}
