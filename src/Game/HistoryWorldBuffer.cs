using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Workers receive detached bytes only. Completed buffers retain compressed
    // bytes; decompress locally for a selected jump or archive encoding.
    internal sealed class HistoryWorldBuffer
    {
        internal readonly int Length;
        readonly Task<byte[]> packed;
        internal HistoryWorldBuffer(byte[] bytes)
        {
            Length=bytes.Length;
            packed=Task.Run(()=>{
                using(var output=new MemoryStream())
                {
                    using(var zip=new GZipStream(output,CompressionLevel.Fastest,true))zip.Write(bytes,0,bytes.Length);
                    return output.ToArray();
                }
            });
        }
        internal byte[] Read()
        {
            var compressed=packed.GetAwaiter().GetResult();
            using(var input=new MemoryStream(compressed))
            using(var zip=new GZipStream(input,CompressionMode.Decompress))
            {
                var result=new byte[Length];int offset=0,count;
                while(offset<result.Length && (count=zip.Read(result,offset,result.Length-offset))>0)offset+=count;
                if(offset!=result.Length || zip.ReadByte()!=-1)throw new InvalidDataException("历史世界快照解压长度不符。");
                return result;
            }
        }
    }
}
