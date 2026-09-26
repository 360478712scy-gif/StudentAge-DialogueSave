using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    // Venetian wipe: the old framebuffer stays fixed in narrowing diagonal
    // slats while the live destination is revealed between them. No black plate.
    internal sealed class AdvSettingsTransition : MonoBehaviour
    {
        internal const float Duration=.30f;
        internal static AdvSettingsTransition Active;
        RenderTexture snapshot;
        SettingsSlats oldFrame,edges;
        float started=-1;
        bool firstFrameRendered;
        System.Collections.IEnumerator Start()
        {
            // Let the opaque source frame and destination fonts/textures render
            // once before starting the wipe clock. First-use upload must not
            // consume most of the 300 ms animation in a single stationary frame.
            yield return new WaitForEndOfFrame();firstFrameRendered=true;
        }
        System.Action completed;
        internal float Progress {get;private set;}
        internal static void Exit(System.Action dismiss,System.Action completed=null,int sortingOrder=32500,bool horizontal=false)
        {
            var snapshot=Capture();
            try {dismiss();Play(snapshot,false,completed,sortingOrder:sortingOrder,horizontal:horizontal);}
            catch {RenderTexture.ReleaseTemporary(snapshot);throw;}
        }
        internal static RenderTexture Capture()
        {
            var target=RenderTexture.GetTemporary(Screen.width,Screen.height,0,RenderTextureFormat.ARGB32);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
            return target;
        }
        internal static void Play(RenderTexture snapshot,bool opening,System.Action completed=null,bool pageChange=false,int sortingOrder=30600,bool horizontal=false)
        {
            if(Active!=null)Destroy(Active.gameObject);
            var root=AdvWidgets.Canvas("Settings diagonal transition",sortingOrder);AdvWidgets.CenterDesign(root);
            var result=root.AddComponent<AdvSettingsTransition>();Active=result;
            result.snapshot=snapshot;result.completed=completed;
            // Transparent input blocker covers the complete transition, including gaps.
            var guard=AdvWidgets.Box("Transition input guard",root.transform,0,0,1920,1080,Color.clear);guard.raycastTarget=true;AdvWidgets.Fill(guard.rectTransform);
            if(horizontal)
            {
                // The snapshot is the whole window; slide it over the whole canvas, not a 16:9 box.
                result.oldFrame=PageSlats(root.transform,snapshot,new Rect(0,0,1920,1080));result.oldFrame.FullScreen=true;AdvWidgets.Fill(result.oldFrame.rectTransform);
            }
            else if(pageChange)
            {
                result.oldFrame=PageSlats(root.transform,snapshot,new Rect(116,142,1688,750));
                result.edges=PageSlats(root.transform,snapshot,new Rect(80,980,1040,78));
            }
            else
            {
                result.oldFrame=AdvWidgets.Rect("Old picture slats",root.transform,0,0,1920,1080).gameObject.AddComponent<SettingsSlats>();
                result.oldFrame.Picture=snapshot;result.oldFrame.Opening=opening;AdvWidgets.Fill(result.oldFrame.rectTransform);
                result.edges=AdvWidgets.Rect("Blue fine separators",root.transform,0,0,1920,1080).gameObject.AddComponent<SettingsSlats>();
                result.edges.Edges=true;result.edges.Opening=opening;AdvWidgets.Fill(result.edges.rectTransform);
            }
            result.oldFrame.raycastTarget=false;if(result.edges!=null)result.edges.raycastTarget=false;
        }
        static SettingsSlats PageSlats(Transform parent,Texture snapshot,Rect region)
        {
            var slats=AdvWidgets.Rect("Page horizontal blinds",parent,region.x,region.y,region.width,region.height).gameObject.AddComponent<SettingsSlats>();
            slats.Picture=snapshot;slats.Horizontal=true;slats.ScreenRegion=region;return slats;
        }
        void Update()
        {
            if(!firstFrameRendered)return;
            if(started<0)started=Time.unscaledTime;
            Progress=Mathf.Clamp01((Time.unscaledTime-started)/Duration);
            oldFrame.Progress=Progress;oldFrame.SetVerticesDirty();
            if(edges!=null){edges.Progress=Progress;edges.SetVerticesDirty();}
            if(Progress>=1){var finish=completed;completed=null;finish?.Invoke();Destroy(gameObject);}
        }
        void OnDestroy()
        {
            if(snapshot!=null)RenderTexture.ReleaseTemporary(snapshot);if(Active==this)Active=null;
            var finish=completed;completed=null;finish?.Invoke();
        }
    }
    internal sealed class SettingsSlats : MaskableGraphic
    {
        internal Texture Picture;
        internal bool Opening,Edges,Horizontal;
        internal Rect ScreenRegion;internal bool FullScreen;
        internal float Progress;
        public override Texture mainTexture=>Picture!=null?Picture:Texture2D.whiteTexture;
        readonly Vector2[] workA=new Vector2[8],workB=new Vector2[8];
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();if(Horizontal){PopulateHorizontal(mesh);return;}var rect=rectTransform.rect;float w=rect.width,h=rect.height;
            float spacing=w*256f/1920f,slope=Opening?-.5f:.5f;
            // Source rule repeats once per 256px; opening and closing mirror the diagonal.
            float travel=Progress*spacing;
            float line=w*12f/1920f*Mathf.Min(1,Mathf.Min(Progress,1-Progress)*12);
            for(int i=-4;i<12;i++)
            {
                float cut=i*spacing+travel;
                float left=Edges?cut-line*.5f:cut;
                float right=Edges?cut+line*.5f:(i+1)*spacing;
                if(right<=left)continue;
                workA[0]=new Vector2(left,0);workA[1]=new Vector2(right,0);
                workA[2]=new Vector2(right+slope*h,h);workA[3]=new Vector2(left+slope*h,h);
                int n=Clip(workA,4,workB,0,true);n=Clip(workB,n,workA,w,false);
                if(n<3)continue;
                int start=mesh.currentVertCount;
                for(int j=0;j<n;j++)
                {
                    var point=workA[j];
                    var ink=Edges?Color.Lerp(new Color32(24,112,221,255),new Color32(111,228,255,255),point.y/h):Color.white;
                    mesh.AddVert(new Vector3(rect.xMin+point.x,rect.yMin+point.y),ink,new Vector2(point.x/w,Picture is RenderTexture && SystemInfo.graphicsUVStartsAtTop?1-point.y/h:point.y/h));
                }
                for(int j=1;j<n-1;j++)mesh.AddTriangle(start,start+j,start+j+1);
            }
        }
        void PopulateHorizontal(VertexHelper mesh)
        {
            var rect=rectTransform.rect;const float band=32;
            // Source page changes reveal horizontal strips in place. The top
            // tabs and footer action buttons stay live outside the two regions.
            for(float top=rect.height;top>0;top-=band)
            {
                float bottom=Mathf.Max(0,top-band),visibleTop=bottom+(top-bottom)*(1-Progress);
                if(visibleTop<=bottom)continue;int first=mesh.currentVertCount;
                AddPageVertex(mesh,rect,0,bottom);AddPageVertex(mesh,rect,rect.width,bottom);
                AddPageVertex(mesh,rect,rect.width,visibleTop);AddPageVertex(mesh,rect,0,visibleTop);
                mesh.AddTriangle(first,first+1,first+2);mesh.AddTriangle(first,first+2,first+3);
            }
        }
        void AddPageVertex(VertexHelper mesh,Rect rect,float x,float y)
        {
            // Page regions are design coordinates inside the centered 1920x1080 area of the canvas.
            var screen=((RectTransform)canvas.rootCanvas.transform).rect;float cw=screen.width,ch=screen.height,u,v;
            if(FullScreen){u=x/rect.width;v=y/rect.height;}
            else{u=((cw-1920f)/2f+ScreenRegion.x+x)/cw;v=((ch-1080f)/2f+1080-ScreenRegion.y-ScreenRegion.height+y)/ch;}
            if(Picture is RenderTexture && SystemInfo.graphicsUVStartsAtTop)v=1-v;
            mesh.AddVert(new Vector3(rect.xMin+x,rect.yMin+y),Color.white,new Vector2(u,v));
        }
        static int Clip(Vector2[] source,int count,Vector2[] target,float boundary,bool minimum)
        {
            if(count==0)return 0;int n=0;var prev=source[count-1];bool prevIn=minimum?prev.x>=boundary:prev.x<=boundary;
            for(int i=0;i<count;i++)
            {
                var current=source[i];bool inside=minimum?current.x>=boundary:current.x<=boundary;
                if(inside!=prevIn){float t=(boundary-prev.x)/(current.x-prev.x);target[n++]=Vector2.Lerp(prev,current,t);}
                if(inside)target[n++]=current;prev=current;prevIn=inside;
            }
            return n;
        }
    }
}
