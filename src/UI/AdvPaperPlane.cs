using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    internal sealed class AdvPaperPlane:MaskableGraphic
    {
        Vector2 P(float x,float y)=>new Vector2(rectTransform.rect.xMin+x*rectTransform.rect.width/32,
            rectTransform.rect.yMin+y*rectTransform.rect.height/32);
        void Triangle(VertexHelper vh,Vector2 a,Vector2 b,Vector2 c,Color ink)
        {int n=vh.currentVertCount;vh.AddVert(a,ink,Vector2.zero);vh.AddVert(b,ink,Vector2.zero);vh.AddVert(c,ink,Vector2.zero);vh.AddTriangle(n,n+1,n+2);}
        void Line(VertexHelper vh,Vector2 a,Vector2 b)
        {
            var d=(b-a).normalized;var n=new Vector2(-d.y,d.x)*.8f;
            var ink=new Color(.09f,.08f,.06f,1);Triangle(vh,a-n,a+n,b+n,ink);Triangle(vh,a-n,b+n,b-n,ink);
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();var a=P(3,17);var b=P(29,28);var c=P(21,4);var d=P(14,12);var e=P(10,4);
            Triangle(vh,a,b,d,color);Triangle(vh,b,c,d,color);Triangle(vh,d,e,P(11,14),color);
            Line(vh,a,b);Line(vh,b,c);Line(vh,c,d);Line(vh,d,e);Line(vh,e,P(11,14));Line(vh,P(11,14),a);
            Line(vh,b,d);Line(vh,P(11,14),b);
        }
    }
}
