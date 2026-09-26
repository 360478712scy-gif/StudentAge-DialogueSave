using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    internal sealed class AdvPaperPlane:MaskableGraphic
    {
        const float DrawTime=.95f,HoldTime=.45f,ScatterTime=.6f,RestTime=.18f;
        const float Cycle=DrawTime+HoldTime+ScatterTime+RestTime;
        static readonly Vector2[] From={new Vector2(3,17),new Vector2(29,28),new Vector2(21,4),new Vector2(14,12),new Vector2(10,4),new Vector2(11,14),new Vector2(29,28),new Vector2(11,14)};
        static readonly Vector2[] To={new Vector2(29,28),new Vector2(21,4),new Vector2(14,12),new Vector2(10,4),new Vector2(11,14),new Vector2(3,17),new Vector2(14,12),new Vector2(29,28)};
        static readonly float[] Lengths=new float[From.Length];
        static readonly float TotalLength;
        float started,nextFrame,cycleTime;
        internal float AnimationTime=>cycleTime;
        static AdvPaperPlane(){for(int i=0;i<From.Length;i++){Lengths[i]=Vector2.Distance(From[i],To[i]);TotalLength+=Lengths[i];}}
        protected override void OnEnable(){base.OnEnable();started=Time.unscaledTime;nextFrame=0;cycleTime=0;SetVerticesDirty();}
        void Update()
        {
            // Only this tiny nested canvas changes; never dirty the dialogue text.
            float now=Time.unscaledTime;if(now<nextFrame)return;
            nextFrame=now+1f/30;cycleTime=Mathf.Repeat(now-started,Cycle);SetVerticesDirty();
        }
        Vector2 P(float x,float y)=>new Vector2(rectTransform.rect.xMin+x*rectTransform.rect.width/32,
            rectTransform.rect.yMin+y*rectTransform.rect.height/32);
        void Triangle(VertexHelper vh,Vector2 a,Vector2 b,Vector2 c,Color ink)
        {int n=vh.currentVertCount;vh.AddVert(a,ink,Vector2.zero);vh.AddVert(b,ink,Vector2.zero);vh.AddVert(c,ink,Vector2.zero);vh.AddTriangle(n,n+1,n+2);}
        void Line(VertexHelper vh,Vector2 a,Vector2 b,float alpha=1)
        {
            if((b-a).sqrMagnitude<.0001f || alpha<=0)return;
            var d=(b-a).normalized;var n=new Vector2(-d.y,d.x)*.7f;
            var ink=color;ink.a*=alpha;
            Triangle(vh,a-n,a+n,b+n,ink);Triangle(vh,a-n,b+n,b-n,ink);
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if(cycleTime>=DrawTime+HoldTime+ScatterTime)return;
            bool scattering=cycleTime>=DrawTime+HoldTime;
            float remaining=TotalLength*Mathf.Clamp01(cycleTime/DrawTime);
            float scatter=scattering?Mathf.Clamp01((cycleTime-DrawTime-HoldTime)/ScatterTime):0;
            float eased=scatter*scatter*(3-2*scatter);
            for(int i=0;i<From.Length;i++)
            {
                var a=From[i];var b=To[i];
                if(!scattering)
                {
                    float part=Mathf.Clamp01(remaining/Lengths[i]);remaining-=Lengths[i];
                    var end=Vector2.Lerp(a,b,part);Line(vh,P(a.x,a.y),P(end.x,end.y));
                    if(remaining<=0)break;
                }
                else for(int half=0;half<2;half++)
                {
                    var start=Vector2.Lerp(a,b,half*.5f);var end=Vector2.Lerp(a,b,(half+1)*.5f);
                    var center=(start+end)*.5f;var radial=(center-new Vector2(16,16)).normalized;
                    var offset=radial*(8+((i+half)%3))*eased;
                    float angle=(((i+half)%2==0)?1:-1)*eased*.55f;
                    var direction=(end-start)*(.5f*(1-.45f*eased));
                    var rotated=new Vector2(direction.x*Mathf.Cos(angle)-direction.y*Mathf.Sin(angle),direction.x*Mathf.Sin(angle)+direction.y*Mathf.Cos(angle));
                    center+=offset;start=center-rotated;end=center+rotated;
                    Line(vh,P(start.x,start.y),P(end.x,end.y),1-eased);
                }
            }
        }
    }
}
