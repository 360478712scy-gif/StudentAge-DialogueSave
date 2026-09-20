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
        public string PayloadSha256;
    }

    public enum SaveStatus { Ready, UnsupportedVersion, Corrupt, Deleted }

    public sealed class SaveRecord
    {
        public int SchemaVersion;
        public SaveHeader Header;
        public string FilePath;
        public SaveStatus Status;
        public string Error;
        public bool IsConflict;
    }
}
