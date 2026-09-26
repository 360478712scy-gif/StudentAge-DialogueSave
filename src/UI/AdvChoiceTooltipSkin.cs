using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Common;

namespace StudentAgeDialogueSave.UI
{
    // A temporary presentation lease on the native, shared description view.
    // Native rich text, condition callbacks, sizing and pointer timing stay intact.
    internal sealed class AdvChoiceTooltipSkin : IDisposable
    {
        readonly List<Action> restore=new List<Action>();
        readonly List<GameObject> artwork=new List<GameObject>();

        internal AdvChoiceTooltipSkin(DescriptionView view)
        {
            foreach(var group in new[]{view.group_short,view.group_vertical,view.itemgroup_item.transform})
            {
                foreach(var image in group.GetComponentsInChildren<Image>(true))
                {
                    if(image.transform!=group && !image.name.ToLowerInvariant().Contains("bg"))continue;
                    var original=image;bool enabled=image.enabled;
                    restore.Add(()=>{if(original!=null)original.enabled=enabled;});
                    image.enabled=false;
                }
                var panel=AdvSettingsSkin.ConditionHelp(group);
                panel.name="ADV.ChoiceCondition.DarkPaper";
                var rect=panel.rectTransform;
                rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;
                rect.offsetMin=Vector2.zero;rect.offsetMax=Vector2.zero;
                panel.transform.SetAsFirstSibling();
                // Fixed cap size: taller/multiline descriptions stretch only paper.
                panel.pixelsPerUnitMultiplier=panel.sprite.rect.height/100;
                var layout=panel.gameObject.AddComponent<LayoutElement>();layout.ignoreLayout=true;
                artwork.Add(panel.gameObject);
            }
            foreach(var label in new[]{view.txtex_short_title,view.txtex_vertical_title,view.txtex_title})
            {
                var original=label;var font=label.font;var material=label.fontSharedMaterial;
                var color=label.color;var margin=label.margin;var style=label.fontStyle;
                restore.Add(()=>{if(original==null)return;original.font=font;original.fontSharedMaterial=material;original.color=color;original.margin=margin;original.fontStyle=style;});
                label.font=AdvWidgets.ReadingFont(font);label.fontSharedMaterial=AdvWidgets.Outline(label.font);
                label.fontStyle=FontStyles.Normal;label.color=new Color32(235,242,248,255);
                label.OnPreRenderText+=LiftConditionInk;
                restore.Add(()=>{if(original!=null)original.OnPreRenderText-=LiftConditionInk;});
                // Breathing room inside the dark single-rim tooltip.
                label.margin=new Vector4(margin.x+12,margin.y+8,margin.z+12,margin.w+8);
            }
        }

        static void LiftConditionInk(TMP_TextInfo info)
        {
            // Native green is designed for a cream tooltip. Lift glyph brightness
            // for charcoal without rewriting tags, status hues, or condition text.
            for(int i=0;i<info.characterCount;i++)
            {
                var c=info.characterInfo[i];if(!c.isVisible)continue;
                var colors=info.meshInfo[c.materialReferenceIndex].colors32;
                for(int v=0;v<4;v++)
                {
                    int index=c.vertexIndex+v;Color color=colors[index];
                    color.r=Mathf.LinearToGammaSpace(color.r);color.g=Mathf.LinearToGammaSpace(color.g);color.b=Mathf.LinearToGammaSpace(color.b);
                    colors[index]=color;
                }
            }
        }

        public void Dispose()
        {
            foreach(var action in restore)action();restore.Clear();
            foreach(var obj in artwork)if(obj!=null){obj.SetActive(false);UnityEngine.Object.Destroy(obj);}
            artwork.Clear();
        }
    }
}
