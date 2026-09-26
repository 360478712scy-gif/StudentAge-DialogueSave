using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.Storage
{
    public sealed class SaveEnvelope
    {
        public int SchemaVersion = 1;
        public string Kind = "checkpoint";
        public SaveHeader Header = new SaveHeader();
        public byte[] World;
        public JObject Dialogue;
        public string EnvelopeSha256;
    }

    public sealed class SaveHeader
    {
        internal SaveHeader DetachedCopy(){var copy=(SaveHeader)MemberwiseClone();copy.ParentRevisionIds=ParentRevisionIds==null?null:(string[])ParentRevisionIds.Clone();return copy;}
        public string SteamId;
        public string RunId;
        public string Category;
        public string LogicalSlot;
        public string RevisionId;
        public string[] ParentRevisionIds = new string[0];
        public string DeviceId;
        public string CreatedUtc;
        public string GameVersion;
        public string PluginVersion;
        public string AdapterVersion;
        public string Speaker;
        public string Summary;
        public string RoleName;
        public string YearLabel;
        public string SeasonLabel;
        public int? SeasonId;
        public string Location;
        public int? Gender;
        public int? GradeState;
        [Newtonsoft.Json.JsonProperty(NullValueHandling=Newtonsoft.Json.NullValueHandling.Ignore)] public string Comment;
        [Newtonsoft.Json.JsonProperty(NullValueHandling=Newtonsoft.Json.NullValueHandling.Ignore)] public string SavedUtc;
        [Newtonsoft.Json.JsonProperty(NullValueHandling=Newtonsoft.Json.NullValueHandling.Ignore)] public string TransactionId;
        [Newtonsoft.Json.JsonProperty(NullValueHandling=Newtonsoft.Json.NullValueHandling.Ignore)] public int? BackgroundId;
        [Newtonsoft.Json.JsonProperty(NullValueHandling=Newtonsoft.Json.NullValueHandling.Ignore)] public int? SpeakerId;
        [Newtonsoft.Json.JsonProperty(NullValueHandling=Newtonsoft.Json.NullValueHandling.Ignore)] public string PreviewImageUrl;
        public string PayloadSha256;
    }

    public enum SaveStatus { Ready, UnsupportedVersion, Corrupt, Deleted, Pending }

    public sealed class SaveRecord
    {
        public int SchemaVersion;
        public SaveHeader Header;
        public string FilePath;
        public SaveStatus Status;
        public string Error;
        public bool IsConflict;
        public int PreviewBackgroundId,PreviewSpeakerId;
        public string PreviewText,PreviewImageUrl;
        public int[] PreviewOptionIds;
    }
}
