using System;
using System.IO;
using System.Collections;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;

namespace StudentAgeDialogueSave.UI
{
    // Request ownership prevents a late hover response repainting another slot.
    internal sealed class ArchivePreview:MonoBehaviour
    {
        Image picture;int generation;string current,fallback,lastFallback;Sprite owned;Texture2D texture;bool atlas;
        internal static void Set(Image image,string url,string fallback=null,bool atlas=false,string lastFallback=null)
        {
            var owner=image.GetComponent<ArchivePreview>()??image.gameObject.AddComponent<ArchivePreview>();
            owner.picture=image;if(owner.current==url && owner.fallback==fallback && owner.atlas==atlas && owner.lastFallback==lastFallback)return;
            owner.current=url;owner.fallback=fallback;owner.lastFallback=lastFallback;owner.atlas=atlas;owner.generation++;owner.Clear();
            if(!string.IsNullOrEmpty(url))owner.Load(url,owner.generation,url==fallback?null:fallback);
            else if(!string.IsNullOrEmpty(fallback))owner.Load(fallback,owner.generation,null);
        }
        void Clear(){picture.sprite=null;picture.color=Color.clear;if(owned!=null)Destroy(owned);if(texture!=null)Destroy(texture);owned=null;texture=null;}
        void Load(string url,int token,string alternate)
        {
            if(url.StartsWith("Mods"))url=Singleton<ModCtrl>.Ins.GetFullUrl(url);
            if(Path.IsPathRooted(url)){StartCoroutine(External(url,token,alternate));return;}
            if(atlas){AtlasMgr.GetSpriteAsync(url,sprite=>Complete(sprite,token,alternate));return;}
            ResMgr.LoadSpriteAsync(Path.Combine("Textures/",LocalizationMgr.GetLocalizeUrl(url)),sprite=>Complete(sprite,token,alternate));
        }
        void Complete(Sprite sprite,int token,string alternate)
        {
            if(this==null || picture==null || generation!=token)return;
            if(sprite==null){if(!string.IsNullOrEmpty(alternate))StartCoroutine(Fallback(alternate,token));return;}
            picture.sprite=sprite;picture.color=Color.white;
        }
        IEnumerator Fallback(string url,int token)
        {
            // Native resource completion iterates a mutable callback list. Never
            // enqueue another load from inside that iteration.
            yield return null;
            if(this!=null && generation==token)Load(url,token,url==lastFallback?null:lastFallback);
        }
        IEnumerator External(string path,int token,string alternate)
        {
            using(var request=UnityWebRequestTexture.GetTexture(new Uri(path).AbsoluteUri))
            {
                yield return request.SendWebRequest();
                if(this==null || generation!=token)yield break;
                if(request.result!=UnityWebRequest.Result.Success){Complete(null,token,alternate);yield break;}
                texture=DownloadHandlerTexture.GetContent(request);owned=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f));Complete(owned,token,null);
            }
        }
        void OnDestroy(){generation++;if(owned!=null)Destroy(owned);if(texture!=null)Destroy(texture);}
    }
}
