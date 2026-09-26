using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    // Keep the source's antialiased rim at its original aspect ratio. Mirroring
    // the undecorated right cap removes the baked-in pick without repainting it.
    internal sealed class AdvChoiceGraphic : MaskableGraphic
    {
        Texture2D artwork;
        public override Texture mainTexture=>artwork;
        internal void SetArtwork(Texture2D value){if(artwork==value)return;artwork=value;SetMaterialDirty();SetVerticesDirty();}
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();if(artwork==null)return;var r=rectTransform.rect;
            float cap=r.height*.5f,edge=1-50f/1264;
            Quad(mesh,r.xMin,r.xMin+cap,r.yMin,r.yMax,1,edge);
            Quad(mesh,r.xMin+cap,r.xMax-cap,r.yMin,r.yMax,90f/1264,edge);
            Quad(mesh,r.xMax-cap,r.xMax,r.yMin,r.yMax,edge,1);
        }
        void Quad(VertexHelper mesh,float left,float right,float bottom,float top,float u0,float u1)
        {
            int i=mesh.currentVertCount;
            mesh.AddVert(new Vector3(left,bottom),color,new Vector2(u0,0));
            mesh.AddVert(new Vector3(left,top),color,new Vector2(u0,1));
            mesh.AddVert(new Vector3(right,top),color,new Vector2(u1,1));
            mesh.AddVert(new Vector3(right,bottom),color,new Vector2(u1,0));
            mesh.AddTriangle(i,i+1,i+2);mesh.AddTriangle(i,i+2,i+3);
        }
    }

    internal sealed class AdvChoiceSkin : MonoBehaviour,IPointerEnterHandler,IPointerExitHandler,IPointerDownHandler,IPointerUpHandler
    {
        Button button;AdvChoiceGraphic graphic;bool over,pressed;string state;
        internal static Button Create(string name,Transform parent,TMP_FontAsset font,string caption,float x,float y,float width,float height,Action click)
        {
            var rect=AdvWidgets.Rect(name,parent,x,y,width,height);
            var g=rect.gameObject.AddComponent<AdvChoiceGraphic>();g.color=Color.white;
            var b=rect.gameObject.AddComponent<AdvButton>();b.targetGraphic=g;b.transition=Selectable.Transition.None;
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;
            var skin=rect.gameObject.AddComponent<AdvChoiceSkin>();skin.button=b;skin.graphic=g;
            foreach(string s in new[]{"idle","hover","pressed","disabled"})AdvSkin.Texture("choice-"+s+".png");
            for(int i=0;i<2;i++)
            {
                var icon=AdvWidgets.Rect(i==0?"Pencil silhouette":"Ruler silhouette",rect,i==0?20:width-74,13,54,54).gameObject.AddComponent<RawImage>();
                icon.texture=AdvSkin.Texture("choice-stationery-mask.png");icon.uvRect=new Rect(i*.5f,0,.5f,1);
                icon.color=new Color(.52f,.72f,.88f,.32f);icon.raycastTarget=false;
            }
            var text=AdvWidgets.Label("Caption",rect,font,caption,96,0,width-192,height,30,new Color(.94f,.91f,.98f,1));
            text.alignment=TextAlignmentOptions.Center;text.richText=true;text.enableWordWrapping=true;
            text.enableAutoSizing=true;text.fontSizeMin=21;text.fontSizeMax=30;text.fontSharedMaterial=AdvWidgets.LightOutline(text.font);
            AdvSkin.PrepareAudio();
            b.onClick.AddListener(()=>{if(!b.interactable)return;click();});
            skin.Refresh();return b;
        }
        void LateUpdate(){Refresh();}
        void Refresh()
        {
            if(button==null)return;
            string next=!button.interactable?"disabled":pressed?"pressed":over?"hover":"idle";
            if(state==next)return;state=next;graphic.SetArtwork(AdvSkin.Texture("choice-"+state+".png"));
        }
        public void OnPointerEnter(PointerEventData e){over=true;Refresh();}
        public void OnPointerExit(PointerEventData e){over=false;Refresh();}
        public void OnPointerDown(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=true;Refresh();}}
        public void OnPointerUp(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=false;Refresh();}}
        void OnDisable(){over=pressed=false;Refresh();}
    }
}
