using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    internal static class AdvBacklogSkin
    {
        static readonly Dictionary<string,Sprite> sprites=new Dictionary<string,Sprite>();
        internal static Sprite Sprite(string name)
        {
            if(sprites.TryGetValue(name,out var value))return value;
            var texture=AdvSkin.Texture(name);
            Rect bounds=new Rect(0,0,texture.width,texture.height);
            if(name=="backlog-date.png")
            {
                var p=texture.GetPixels32();int left=texture.width,bottom=texture.height,right=0,top=0;
                for(int y=0;y<texture.height;y++)for(int x=0;x<texture.width;x++)if(p[y*texture.width+x].a>160)
                {left=Math.Min(left,x);right=Math.Max(right,x);bottom=Math.Min(bottom,y);top=Math.Max(top,y);}
                bounds=Rect.MinMaxRect(left,bottom,right+1,top+1);
            }
            value=UnityEngine.Sprite.Create(texture,bounds,new Vector2(.5f,.5f),100);sprites.Add(name,value);return value;
        }
        internal static System.Collections.IEnumerator Warm()
        {
            foreach(string name in new[]{"backlog-frame.png","backlog-date.png"}){yield return null;Sprite(name);}
            foreach(string control in new[]{"jump","top","end","pageup","pagedown"})
                foreach(string state in new[]{"off","over","on"}){yield return null;Sprite("backlog-"+control+"_"+state+".png");}
        }
        internal static void Prepare()
        {
            Sprite("backlog-frame.png");Sprite("backlog-date.png");
            foreach(string control in new[]{"jump","top","end","pageup","pagedown"})
                foreach(string state in new[]{"off","over","on"})Sprite("backlog-"+control+"_"+state+".png");
        }
        internal static Button Icon(string name,string asset,Transform parent,float x,float y,Action action)
        {
            var root=AdvWidgets.Rect(name,parent,x,y,44,44);
            var hit=root.gameObject.AddComponent<Image>();hit.color=Color.clear;
            var b=root.gameObject.AddComponent<AdvButton>();b.transition=Selectable.Transition.SpriteSwap;
            var art=AdvWidgets.Rect("Source icon",root,22,22,44,44).gameObject.AddComponent<Image>();
            art.rectTransform.pivot=new Vector2(.5f,.5f);art.rectTransform.localEulerAngles=new Vector3(0,0,-90);art.raycastTarget=false;
            art.sprite=Sprite("backlog-"+asset+"_off.png");b.targetGraphic=art;
            b.spriteState=new SpriteState{highlightedSprite=Sprite("backlog-"+asset+"_over.png"),pressedSprite=Sprite("backlog-"+asset+"_on.png"),disabledSprite=art.sprite};
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;
            b.onClick.AddListener(()=>{if(!b.interactable)return;action();});return b;
        }
        internal static Button Footer(Transform parent,TMP_FontAsset font,Action close)
        {
            var image=AdvSettingsSkin.Control("ADV.CloseHistory",parent,2,1552,984,296,76);
            var b=image.gameObject.AddComponent<AdvButton>();b.targetGraphic=image;AdvSettingsSkin.Style(b,2);
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;
            var text=AdvSettingsSkin.Lettering("返回对话",image.transform,70,24,218,28);
            b.gameObject.AddComponent<SettingsButtonInk>().Text=text;
            b.onClick.AddListener(()=>{close();});return b;
        }
        internal static void Release(){foreach(var sprite in sprites.Values)UnityEngine.Object.Destroy(sprite);sprites.Clear();}
    }
}
