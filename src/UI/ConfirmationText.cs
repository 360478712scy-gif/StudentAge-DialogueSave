using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StudentAgeDialogueSave.UI
{
    internal static class ConfirmationText
    {
        // Measure once when the prompt opens. Balanced lines avoid a trailing
        // single character; text elements keep emoji/surrogate pairs intact.
        internal static string Wrap(string text,float maximum,Func<string,float> measure)
        {
            if(string.IsNullOrEmpty(text))return text??"";
            var result=new List<string>();
            foreach(string paragraph in text.Replace("\r\n","\n").Split('\n'))
            {
                if(measure(paragraph)<=maximum){result.Add(paragraph);continue;}
                var letters=new List<string>();var enumerator=StringInfo.GetTextElementEnumerator(paragraph);
                while(enumerator.MoveNext())letters.Add((string)enumerator.Current);
                int start=0;
                while(start<letters.Count)
                {
                    string rest=string.Concat(letters.GetRange(start,letters.Count-start));float width=measure(rest);
                    if(width<=maximum){result.Add(rest);break;}
                    int rows=Math.Max(2,(int)Math.Ceiling(width/maximum));float target=width/rows;
                    var prefix=new StringBuilder();int best=-1,fit=0;float score=float.MaxValue;
                    for(int i=start;i<letters.Count-1;i++)
                    {
                        prefix.Append(letters[i]);float used=measure(prefix.ToString());if(used>maximum)break;fit=i+1;
                        if(letters.Count-i-1==1 || !CanBreak(letters[i],letters[i+1]))continue;
                        float cost=Math.Abs(used-target);if("，。；！？、,;!?".Contains(letters[i]))cost-=8;
                        if(cost<score){score=cost;best=i+1;}
                    }
                    if(best<0)best=fit>start?fit:start+1;
                    result.Add(string.Concat(letters.GetRange(start,best-start)));start=best;
                }
            }
            return string.Join("\n",result);
        }
        static bool CanBreak(string left,string right)
        {
            if("，。！？；：、）》】」』,.!?;:)]".Contains(right) || "（《【「『([".Contains(left))return false;
            bool a=left.Length==1 && left[0]<128 && char.IsLetterOrDigit(left[0]);
            bool b=right.Length==1 && right[0]<128 && char.IsLetterOrDigit(right[0]);return !(a&&b);
        }
    }
}
