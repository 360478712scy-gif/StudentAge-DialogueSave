using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.Storage
{
    internal static class SaveCodec
    {
        internal const int MaximumFileBytes = 96 * 1024 * 1024;
        internal const int MaximumWorldBytes = 32 * 1024 * 1024;
        internal const int MaximumDialogueBytes = 8 * 1024 * 1024;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static JsonSerializer Serializer()
        {
            return JsonSerializer.Create(new JsonSerializerSettings {
                TypeNameHandling = TypeNameHandling.None,
                MissingMemberHandling = MissingMemberHandling.Error,
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 64,
                Culture = CultureInfo.InvariantCulture
            });
        }

        // Build tokens ourselves so duplicate keys cannot silently replace metadata.
        internal static JObject Read(string path)
        {
            return Read(ReadBytes(path));
        }
        internal static byte[] ReadBytes(string path)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<=0 || stream.Length>MaximumFileBytes)throw new InvalidDataException("存档大小超出限制。");
                var bytes=new byte[(int)stream.Length];int offset=0,count;
                while(offset<bytes.Length && (count=stream.Read(bytes,offset,bytes.Length-offset))>0)offset+=count;
                if(offset!=bytes.Length || stream.ReadByte()!=-1)throw new InvalidDataException("读取时存档长度发生变化。");
                return bytes;
            }
        }
        internal static JObject Read(byte[] bytes)
        {
            using(var stream=new MemoryStream(bytes,false))
            {
                using (var text = new StreamReader(stream, Utf8, false))
                using (var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
                {
                    if (!reader.Read()) throw new InvalidDataException("空存档。");
                    var root = ReadToken(reader) as JObject;
                    if (root == null || reader.Read()) throw new InvalidDataException("存档结构无效。");
                    return root;
                }
            }
        }

        private static JToken ReadToken(JsonTextReader reader)
        {
            if (reader.TokenType == JsonToken.StartObject)
            {
                var result = new JObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonToken.EndObject)
                {
                    if (reader.TokenType != JsonToken.PropertyName) throw new InvalidDataException("无效字段。");
                    string name = (string)reader.Value;
                    if (!names.Add(name) || name == "$type") throw new InvalidDataException("重复或不允许的字段。");
                    if (!reader.Read()) throw new InvalidDataException("存档不完整。");
                    result.Add(name, ReadToken(reader));
                }
                if (reader.TokenType != JsonToken.EndObject) throw new InvalidDataException("存档不完整。");
                return result;
            }
            if (reader.TokenType == JsonToken.StartArray)
            {
                var result = new JArray();
                while (reader.Read() && reader.TokenType != JsonToken.EndArray) result.Add(ReadToken(reader));
                if (reader.TokenType != JsonToken.EndArray) throw new InvalidDataException("存档不完整。");
                return result;
            }
            if (reader.TokenType == JsonToken.String || reader.TokenType == JsonToken.Integer ||
                reader.TokenType == JsonToken.Float || reader.TokenType == JsonToken.Boolean)
                return new JValue(reader.Value);
            if (reader.TokenType == JsonToken.Null) return JValue.CreateNull();
            throw new InvalidDataException("存档包含不允许的 JSON 内容。");
        }

        internal static JObject Token(SaveEnvelope envelope) { return JObject.FromObject(envelope, Serializer()); }

        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static string PayloadHash(SaveEnvelope value)
        {
            // An explicit world length separates arbitrary world bytes from dialogue JSON.
            byte[] dialogue = Utf8.GetBytes(value.Dialogue.ToString(Formatting.None));
            if (dialogue.Length > MaximumDialogueBytes) throw new InvalidDataException("对话状态过大。");
            using (var sha=SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(value.World.Length); writer.Write(value.World);
                writer.Write(dialogue.Length); writer.Write(dialogue); writer.Flush();
                stream.FlushFinalBlock();return Hex(sha.Hash);
            }
        }

        internal static string EnvelopeHash(JObject token)
        {
            using(var sha=SHA256.Create())
            using(var stream=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write))
            using(var text=new StreamWriter(stream,Utf8,4096,true))
            using(var writer=new JsonTextWriter(text){Formatting=Formatting.None,Culture=CultureInfo.InvariantCulture})
            {
                writer.WriteStartObject();
                foreach(var property in token.Properties())if(property.Name!="EnvelopeSha256")property.WriteTo(writer);
                writer.WriteEndObject();writer.Flush();text.Flush();stream.FlushFinalBlock();return Hex(sha.Hash);
            }
        }
        static string Hex(byte[] hash)=>BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        internal static SaveEnvelope Decode(JObject token)
        {
            if ((int?)token["SchemaVersion"] != 1) throw new InvalidDataException("不支持的存档版本。");
            var value = token.ToObject<SaveEnvelope>(Serializer());
            Validate(value);
            if (value.Header.PayloadSha256 != PayloadHash(value) || value.EnvelopeSha256 != EnvelopeHash(token))
                throw new InvalidDataException("存档校验失败。");
            return value;
        }

        internal static void Validate(SaveEnvelope value)
        {
            if (value == null || value.Header == null || value.World == null ||
                value.World.Length > MaximumWorldBytes || value.Dialogue == null)
                throw new InvalidDataException("存档缺少完整世界或对话数据。");
            if (value.Kind != "checkpoint" && value.Kind != "tombstone") throw new InvalidDataException("未知存档条目类型。");
            if (value.Kind == "checkpoint" && value.World.Length == 0) throw new InvalidDataException("存档世界数据为空。");
            if (value.Kind == "tombstone" && (value.World.Length != 0 || value.Dialogue.Count != 0 || value.Header.ParentRevisionIds == null || value.Header.ParentRevisionIds.Length == 0))
                throw new InvalidDataException("无效删除标记。");
            ValidateHeader(value.Header, value.Kind == "tombstone" ? 8192 : 16);
        }

        internal static void ValidateHeader(SaveHeader h, int maximumParents = 16)
        {
            Required(h.SteamId, 32); Required(h.RunId, 128); Required(h.LogicalSlot, 128);
            Required(h.DeviceId, 128); Required(h.AdapterVersion, 128);
            Required(h.GameVersion, 128); Required(h.PluginVersion, 128);
            if (h.Category != "manual" && h.Category != "auto" && h.Category != "quick")
                throw new InvalidDataException("未知对话存档分类。");
            Revision(h.RevisionId);
            DateTime timestamp;
            if (!DateTime.TryParseExact(h.CreatedUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp) || timestamp.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("无效存档时间。");
            if (h.ParentRevisionIds == null || h.ParentRevisionIds.Length > maximumParents) throw new InvalidDataException("无效父版本。");
            var parents = new HashSet<string>(StringComparer.Ordinal);
            foreach (string parent in h.ParentRevisionIds)
            {
                Revision(parent);
                if (parent == h.RevisionId || !parents.Add(parent)) throw new InvalidDataException("无效父版本。");
            }
            if ((h.Speaker != null && h.Speaker.Length > 512) || (h.Summary != null && h.Summary.Length > 4096))
                throw new InvalidDataException("存档摘要过长。");
            if(h.TransactionId!=null)Revision(h.TransactionId);
            if(h.SavedUtc!=null && (!DateTime.TryParseExact(h.SavedUtc,"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var savedAt) || savedAt.Kind!=DateTimeKind.Utc))throw new InvalidDataException("无效原保存时间。");
            if(h.Comment!=null && h.Comment.Length>4096)throw new InvalidDataException("备注过长。");
            foreach (string label in new[] { h.RoleName, h.YearLabel, h.SeasonLabel, h.Location })
                if (label != null && label.Length > 512) throw new InvalidDataException("存档显示字段过长。");
        }

        private static void Required(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > max) throw new InvalidDataException("存档标识缺失或过长。");
        }

        internal static void Revision(string revision)
        {
            Guid parsed;
            if (revision == null || revision.Length != 32 || !Guid.TryParseExact(revision, "N", out parsed) || revision != parsed.ToString("N"))
                throw new InvalidDataException("无效版本标识。");
        }

        internal static SaveHeader FutureHeader(JObject root)
        {
            // Higher schemas remain display-only. Select known fields, never instantiate a type from file metadata.
            var source = root["Header"] as JObject;
            if (source == null) throw new InvalidDataException("存档头部缺失。");
            var known = JObject.FromObject(new SaveHeader(), Serializer());
            var filtered = new JObject();
            foreach (var property in known.Properties()) if (source[property.Name] != null) filtered[property.Name] = source[property.Name].DeepClone();
            return filtered.ToObject<SaveHeader>(Serializer());
        }
    }
}
