using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.GameIntegration
{
    public sealed class CheckpointBrief
    {
        public string SteamId;
        public string RunId;
        public string Speaker;
        public string Summary;
        public string GameVersion;
        public string StableNodeKey;
        public string RoleName;
        public string YearLabel;
        public string SeasonLabel;
        public int SeasonId;
        public string Location;
        public int Gender;
        public int GradeState;
        public int Round;
    }

    public sealed class GameCheckpoint
    {
        internal HistoryCheckpoint[] HistoryTrail;
        public JObject Dialogue;
        public byte[] WorldBytes;
        public CheckpointBrief Brief;
    }
}
