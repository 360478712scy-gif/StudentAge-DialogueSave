using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Main;
public static class RuntimeFontQA
{
    public static void Capture(string output)
    {
        var group=((TopView)UIMgr.GetView<TopView>()).itemgroup_key.transform;
        var cells=new JArray(group.Cast<Transform>().Where(t=>t.gameObject.activeInHierarchy).Select(t=>new JObject {
            ["name"]=t.name,["texts"]=new JArray(t.GetComponentsInChildren<TMP_Text>(true).Select(Describe))
        }));
        File.WriteAllText(Path.Combine(output,"font-properties.json"),cells.ToString());
        File.WriteAllText(Path.Combine(output,"loaded-fonts.json"),new JArray(Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
            .Select(f=>new JObject{["name"]=f.name,["family"]=f.faceInfo.familyName,["style"]=f.faceInfo.styleName,
                ["normalStyle"]=f.normalStyle,["boldStyle"]=f.boldStyle})).ToString());
    }
    static JObject Describe(TMP_Text t)
    {
        var m=t.fontSharedMaterial;
        var props=new JObject();
        foreach(string p in new[]{"_FaceDilate","_OutlineWidth","_OutlineSoftness","_WeightNormal","_WeightBold","_GradientScale"})
            if(m!=null&&m.HasProperty(p))props[p]=m.GetFloat(p);
        return new JObject{["name"]=t.name,["text"]=t.text,["font"]=t.font?.name,["family"]=t.font?.faceInfo.familyName,
            ["material"]=m?.name,["style"]=t.fontStyle.ToString(),["weight"]=t.fontWeight.ToString(),
            ["size"]=t.fontSize,["autosize"]=t.enableAutoSizing,["richText"]=t.richText,["spacing"]=t.characterSpacing,
            ["scale"]=t.transform.lossyScale.ToString(),["color"]=t.color.ToString(),["gradient"]=t.enableVertexGradient,
            ["materialProperties"]=props};
    }
}
