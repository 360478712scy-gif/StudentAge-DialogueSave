using Components;
using Sdk;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using View.Common;

namespace StudentAgeDialogueSave.UI
{
    // Native options own eligibility and condition descriptions. Mirror presentation
    // without re-evaluating conditions or turning the hidden native UI back on.
    internal sealed class AdvChoiceBinding : MonoBehaviour,IPointerExitHandler
    {
        Button target;
        Sdk.UIButton native;
        Description description;
        bool? available;
        GameObject tooltipLayer;
        Transform tooltip,tooltipParent;
        int tooltipSibling;
        AdvChoiceTooltipSkin tooltipSkin;

        internal void Bind(Button target,Sdk.UIButton native)
        {
            this.target=target;this.native=native;
            var colors=target.colors;
            float gray=colors.normalColor.grayscale<.3f?.32f:.70f;
            colors.disabledColor=new Color(gray,gray,gray,colors.normalColor.a);
            target.colors=colors;
            var source=native?.descObj==null?null:native.descObj.GetComponent<Description>();
            if(source!=null)
            {
                description=gameObject.AddComponent<Description>();
                description.getDescription=source.getDescription;
            }
            LateUpdate();
        }

        void LateUpdate()
        {
            SyncTooltipLayer();
            bool current=native?.btn!=null && native.interactable;
            if(available==current || target==null)return;
            available=current;target.interactable=current;
        }

        void OnDisable()
        {
            if(description!=null)description.OnPointerExit(null);
            RestoreTooltipLayer();
        }

        public void OnPointerExit(PointerEventData eventData)
        {RestoreTooltipLayer();}

        void SyncTooltipLayer()
        {
            if(description==null || !description.isEnter || Singleton<DescCtrl>.Ins.comp!=description || !UIMgr.IsViewOpened<DescriptionView>())
            {RestoreTooltipLayer();return;}
            if(tooltipLayer!=null)return;
            var view=(DescriptionView)UIMgr.GetView<DescriptionView>();
            var nativeCanvas=view.gameObject.GetComponentInParent<Canvas>();
            if(nativeCanvas==null)return;
            // Camera-space native tips would otherwise be covered by ADV's overlay
            // choices. Temporarily host only this tip above ADV, preserving its scale.
            tooltipLayer=AdvWidgets.Canvas("DialogueSave.ADV.OptionTooltip",30000);
            tooltipLayer.GetComponent<GraphicRaycaster>().enabled=false;
            var scaler=tooltipLayer.GetComponent<CanvasScaler>();
            scaler.uiScaleMode=CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor=nativeCanvas.rootCanvas.scaleFactor;
            scaler.referencePixelsPerUnit=nativeCanvas.rootCanvas.referencePixelsPerUnit;
            tooltip=view.gameObject.transform;tooltipParent=tooltip.parent;tooltipSibling=tooltip.GetSiblingIndex();
            tooltip.SetParent(tooltipLayer.transform,false);
            tooltipSkin=new AdvChoiceTooltipSkin(view);
        }

        void RestoreTooltipLayer()
        {
            tooltipSkin?.Dispose();tooltipSkin=null;
            if(tooltip!=null && tooltipParent!=null)
            {tooltip.SetParent(tooltipParent,false);tooltip.SetSiblingIndex(tooltipSibling);}
            tooltip=null;tooltipParent=null;
            if(tooltipLayer!=null)Destroy(tooltipLayer);
            tooltipLayer=null;
        }
    }
}
