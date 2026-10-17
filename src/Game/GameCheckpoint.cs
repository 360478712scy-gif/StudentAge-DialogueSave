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
        byte[] worldBytes;
        HistoryWorldBuffer packedWorld;
        internal CheckpointHistoryLog NativeHistory;
        public byte[] WorldBytes {get=>worldBytes??packedWorld?.Read();set{worldBytes=value;packedWorld=null;}}
        internal object WorldIdentity=>(object)packedWorld??worldBytes;
        internal int WorldLength=>worldBytes?.Length??packedWorld?.Length??0;
        internal void PackHistoryWorld(){if(worldBytes==null)return;packedWorld=new HistoryWorldBuffer(worldBytes);worldBytes=null;}
        internal void ShareHistoryWorld(GameCheckpoint source){packedWorld=source.packedWorld;worldBytes=source.worldBytes;}
        internal int HistoryCount=>NativeHistory?.Count??(Dialogue["history"] as JArray)?.Count??0;
        internal JObject MaterializeDialogue()
        {
            var result=(JObject)Dialogue.DeepClone();
            if(NativeHistory!=null)result["history"]=NativeHistory.Materialize();
            return result;
        }
        public CheckpointBrief Brief;
    }
}
