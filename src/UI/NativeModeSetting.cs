using System;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Main;

namespace StudentAgeDialogueSave.UI
{
    // Native arrows edit a draft. SettingView's own confirm/cancel remains the
    // authority, so changing the preview value never swaps UI under the pointer.
    internal sealed class NativeModeSetting : MonoBehaviour
    {
        RectTransform row;
        Text value;
        UIButton previous,next;
        AdvDialogueController owner;
        string draft;
        bool editing;

        internal void Begin(SettingView view,AdvDialogueController controller)
        {
            if(editing)return; // Controller scans open native views; do not reset an in-progress draft.
            owner=controller;draft=owner.Mode;editing=true;
            AccessTools.Field(typeof(SettingView),"isConfirm").SetValue(view,false);
            if(row==null)Build(view);
            row.gameObject.SetActive(true);Refresh();
        }
        void Build(SettingView view)
        {
            var source=view.line_txtSpeedUp;
            row=NativeUiCloner.Clone(source.gameObject,source.parent,"DialogueSave.ModeSetting").GetComponent<RectTransform>();
            row.SetSiblingIndex(source.GetSiblingIndex()+1);
            // The source line carries a speed-only tooltip; do not inherit it.
            foreach(var description in row.GetComponentsInChildren<Components.Description>(true))
            {description.enabled=false;Destroy(description);}
            value=row.Find("group/txt_txtSpeedUp").GetComponent<Text>();
            foreach(var label in row.GetComponentsInChildren<Text>(true))if(label!=value)label.text="对话界面";
            previous=Bind(row.Find("group/btn_txtSpeedUpPrev").gameObject,view.btn_txtSpeedUpPrev,"DialogueSave.ModePrevious",()=>Set("Original"));
            next=Bind(row.Find("group/btn_txtSpeedUpNext").gameObject,view.btn_txtSpeedUpNext,"DialogueSave.ModeNext",()=>Set("ADV"));
            // Respect native layout groups where present; otherwise use the
            // existing line-to-line pitch, keeping every original row untouched.
            if(source.parent.GetComponent<VerticalLayoutGroup>()==null)
            {
                float pitch=float.MaxValue;
                foreach(Transform sibling in source.parent)
                {
                    var other=sibling as RectTransform;
                    if(other==null || other==source || other==row)continue;
                    float delta=other.anchoredPosition.y-source.anchoredPosition.y;
                    if(delta>source.rect.height*.5f)pitch=Mathf.Min(pitch,delta);
                }
                if(pitch==float.MaxValue)pitch=source.rect.height+20;
                row.anchoredPosition=source.anchoredPosition-new Vector2(0,pitch);
            }
        }
        static UIButton Bind(GameObject target,UIButton source,string name,Action action)
        {
            target.name=name;
            var button=new UIButton(target);
            button.SetSound(source.soundChannel,source.clickSoundUrl,source.clickSoundVolumn,source.hoverSoundUrl,source.hoverSoundVolumn);
            button.AddClick(()=>action());return button;
        }
        void Set(string mode){draft=mode;Refresh();}
        void Refresh()
        {
            value.text=draft=="ADV"?"ADV":"原版";
            previous.gameObject.SetActive(draft=="ADV");next.gameObject.SetActive(draft!="ADV");
        }
        internal void Complete(SettingView view)
        {
            if(!editing)return;editing=false;
            bool confirmed=(bool)AccessTools.Field(typeof(SettingView),"isConfirm").GetValue(view);
            if(confirmed && draft!=owner.Mode)owner.SelectMode(draft);
        }
        internal void Hide(){editing=false;if(row!=null)row.gameObject.SetActive(false);}
        internal static void Release()
        {foreach(var setting in Resources.FindObjectsOfTypeAll<NativeModeSetting>()){setting.Hide();Destroy(setting);}}
        void OnDestroy(){if(row!=null)Destroy(row.gameObject);}
    }
}
