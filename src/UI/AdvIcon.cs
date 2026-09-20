using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace StudentAgeDialogueSave.UI
{
    // One lightweight vector mesh per symbol, independent of font glyph coverage.
    internal sealed class AdvIcon : MaskableGraphic
    {
        internal int Symbol;
        Vector2 P(float x,float y)=>new Vector2(rectTransform.rect.xMin+x/32*rectTransform.rect.width,
            rectTransform.rect.yMin+y/32*rectTransform.rect.height);
        void Stroke(VertexHelper vh,float x,float y,float u,float v,float width=1.7f)
        {
            Vector2 a=P(x,y),b=P(u,v),n=(b-a).normalized; n=new Vector2(-n.y,n.x)*width*rectTransform.rect.width/64;
            int s=vh.currentVertCount;vh.AddVert(a-n,color,Vector2.zero);vh.AddVert(a+n,color,Vector2.zero);
            vh.AddVert(b+n,color,Vector2.zero);vh.AddVert(b-n,color,Vector2.zero);
            vh.AddTriangle(s,s+1,s+2);vh.AddTriangle(s,s+2,s+3);
        }
        void Path(VertexHelper vh,params float[] p){for(int i=2;i<p.Length;i+=2)Stroke(vh,p[i-2],p[i-1],p[i],p[i+1]);}
        void Circle(VertexHelper vh,float cx,float cy,float radius)
        {for(int i=0;i<28;i++){float a=i*Mathf.PI/14,b=(i+1)*Mathf.PI/14;Stroke(vh,cx+Mathf.Cos(a)*radius,cy+Mathf.Sin(a)*radius,cx+Mathf.Cos(b)*radius,cy+Mathf.Sin(b)*radius);}}
        void Disk(VertexHelper vh){Path(vh,6,5,26,5,26,23,22,27,6,27,6,5);Path(vh,10,27,10,19,22,19,22,27);Path(vh,11,5,11,13,21,13,21,5);}
        void Folder(VertexHelper vh){Path(vh,5,8,27,8,27,22,16,22,13,26,5,26,5,8);Path(vh,12,15,16,19,20,15);Stroke(vh,16,19,16,10);}
        void SmoothPath(VertexHelper vh,List<Vector2> path)
        {
            int start=vh.currentVertCount;
            float half=1.15f*rectTransform.rect.width/32,fringe=.65f*rectTransform.rect.width/32;
            for(int i=0;i<path.Count;i++)
            {
                Vector2 tangent=(path[Math.Min(i+1,path.Count-1)]-path[Math.Max(0,i-1)]).normalized;
                var normal=new Vector2(-tangent.y,tangent.x);var transparent=color;transparent.a=0;
                vh.AddVert(path[i]-normal*(half+fringe),transparent,Vector2.zero);
                vh.AddVert(path[i]-normal*half,color,Vector2.zero);
                vh.AddVert(path[i]+normal*half,color,Vector2.zero);
                vh.AddVert(path[i]+normal*(half+fringe),transparent,Vector2.zero);
                if(i==0)continue;
                int p=start+(i-1)*4;
                for(int j=0;j<3;j++){vh.AddTriangle(p+j,p+j+4,p+j+1);vh.AddTriangle(p+j+1,p+j+4,p+j+5);}
            }
        }
        void ReturnArrow(VertexHelper vh)
        {
            var curve=new List<Vector2>{P(6,24),P(17,24)};
            for(int i=1;i<=32;i++)
            {float angle=Mathf.PI/2-i*Mathf.PI/32;curve.Add(P(17+8*Mathf.Cos(angle),16+8*Mathf.Sin(angle)));}
            curve.Add(P(11,8));SmoothPath(vh,curve);
            SmoothPath(vh,new List<Vector2>{P(12,29),P(6,24),P(12,19)});
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();switch(Symbol)
            {
                case 0: Path(vh,7,5,25,5,25,27,7,27,7,5);Stroke(vh,11,21,21,21);Stroke(vh,11,16,21,16);Stroke(vh,11,11,18,11);break;
                case 1: Disk(vh);break;
                case 2: Folder(vh);break;
                case 3: Disk(vh);Path(vh,27,16,23,10,28,10,25,3);break;
                case 4: Folder(vh);Path(vh,27,16,23,10,28,10,25,3);break;
                case 5: Path(vh,10,8,24,16,10,24,10,8);break;
                case 10: Path(vh,7,8,21,16,7,24,7,8);Stroke(vh,26,8,26,24);break;
                case 11: Path(vh,8,11,16,19,24,11);Path(vh,8,18,16,26,24,18);break;
                case 12: Path(vh,8,21,16,13,24,21);Path(vh,8,14,16,6,24,14);break;
                case 13: ReturnArrow(vh);break;
                case 9: Path(vh,10,23,18,16,10,9);Path(vh,18,23,26,16,18,9);break;
                case 6: Path(vh,5,8,17,16,5,24,5,8);Path(vh,17,8,29,16,17,24,17,8);break;
                case 7: Path(vh,3,16,8,22,16,25,24,22,29,16,24,10,16,7,8,10,3,16);Circle(vh,16,16,4);break;
                case 8: Circle(vh,16,16,8);Circle(vh,16,16,3);for(int i=0;i<8;i++){float a=i*Mathf.PI/4;Stroke(vh,16+Mathf.Cos(a)*8,16+Mathf.Sin(a)*8,16+Mathf.Cos(a)*12,16+Mathf.Sin(a)*12,2.5f);}break;
                default:Path(vh,10,23,18,16,10,9);Path(vh,18,23,26,16,18,9);break;
            }
        }
    }
    internal sealed class AdvHint : MonoBehaviour,IPointerEnterHandler,IPointerExitHandler
    {
        internal Action<bool> Show;
        public void OnPointerEnter(PointerEventData e)=>Show?.Invoke(true);
        public void OnPointerExit(PointerEventData e)=>Show?.Invoke(false);
        void OnDisable()=>Show?.Invoke(false);
    }
}
