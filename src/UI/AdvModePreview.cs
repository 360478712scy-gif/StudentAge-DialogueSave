using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    // Small bundled screenshots, loaded only while the interface chooser is open.
    // They do not instantiate another dialogue or borrow the game's asset lifetime.
    internal sealed class AdvModePreview : MonoBehaviour
    {
        Texture2D texture;
        internal void Load(string mode)
        {
            using(var stream=typeof(AdvModePreview).Assembly.GetManifestResourceStream("DialogueSave.Preview."+mode+".jpg"))
            using(var bytes=new MemoryStream())
            {
                if(stream==null)throw new FileNotFoundException("Missing bundled interface preview: "+mode);
                stream.CopyTo(bytes);
                texture=new Texture2D(2,2,TextureFormat.RGB24,false){name="ADV.Preview."+mode,hideFlags=HideFlags.HideAndDontSave};
                texture.LoadImage(bytes.ToArray(),true);
                var image=gameObject.AddComponent<RawImage>();image.texture=texture;image.raycastTarget=false;
            }
        }
        void OnDestroy(){if(texture!=null)Destroy(texture);}
    }
}
