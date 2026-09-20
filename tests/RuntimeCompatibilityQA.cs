using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using UnityEngine;

// Compiled only into the isolated QA driver, never into the distributed plugin.
public static class RuntimeCompatibilityQA
{
    const string QaId = "local.studentage.dialoguesave.qa";
    const string QaVersion = "0.1.0";
    const string PatchId = "local.studentage.dialoguesave.qa.fingerprint";
    static bool installed;
    static readonly MethodInfo Match = AccessTools.Method(typeof(DialogueCheckpointAdapter), "PluginFingerprintsMatch");

    public static void Install(string root, Action<bool, string> check)
    {
        string full = Full(root);
        Require(full.EndsWith("/student-age-dialogue-save/qa/runtime", StringComparison.OrdinalIgnoreCase), "QA root suffix");
        Require(string.Equals(full, Full(Paths.GameRootPath), StringComparison.OrdinalIgnoreCase), "active game root");
        Require(Application.companyName == "DlgSaveQA" && Application.productName == "DialogSave", "native QA identity");
        Require(Full(PathDefine.SAVE_PATH).StartsWith(full + "/data/", StringComparison.OrdinalIgnoreCase), "managed QA save path");
        string proof = Path.Combine(root, "isolation-verified.txt");
        Require(File.Exists(proof) && File.ReadAllText(proof).StartsWith("All managed assemblies rescanned;", StringComparison.Ordinal), "managed isolation proof");
        var infos = Chainloader.PluginInfos.Values.ToArray();
        Require(infos.Length == 2 && infos.Count(p => p.Metadata.GUID == DialogueSavePlugin.Id) == 1 &&
            infos.Count(p => p.Metadata.GUID == QaId) == 1, "exact two-plugin QA set");
        var qa = infos.Single(p => p.Metadata.GUID == QaId);
        var own = infos.Single(p => p.Metadata.GUID == DialogueSavePlugin.Id);
        Require(qa.Metadata.Version.ToString() == QaVersion && qa.Instance.GetType().Assembly == typeof(RuntimeCompatibilityQA).Assembly,
            "QA driver identity and version");
        Require(own.Instance.GetType().Assembly == typeof(DialogueSavePlugin).Assembly &&
            own.Metadata.Version.ToString() == DialogueSavePlugin.Version, "production plugin identity and version");
        foreach (var info in infos)
            Require(Full(info.Instance.GetType().Assembly.Location).StartsWith(full + "/BepInEx/plugins/", StringComparison.OrdinalIgnoreCase), "plugin resides in QA root");
        Require(Match != null, "fingerprint comparison contract");
        if (!installed)
        {
            new Harmony(PatchId).Patch(Match, prefix: new HarmonyMethod(typeof(RuntimeCompatibilityQA), nameof(NormalizePrefix)));
            installed = true;
        }
        check?.Invoke(true, "QA-only fingerprint normalization installed after isolation checks");
    }

    static string Full(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
    static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException("QA fingerprint isolation refused: " + reason);
    }

    static bool HasValidQa(JArray entries)
    {
        if (entries == null || entries.Any(e => !(e is JObject) || e["guid"]?.Type != JTokenType.String)) return false;
        if (entries.Select(e => (string)e["guid"]).Distinct(StringComparer.Ordinal).Count() != entries.Count) return false;
        var matches = entries.Where(e => (string)e["guid"] == QaId).ToArray();
        return matches.Length == 1 && matches[0]["version"]?.Type == JTokenType.String && (string)matches[0]["version"] == QaVersion;
    }

    static void NormalizePrefix(ref JArray saved, ref JArray current)
    {
        // Invalid/missing/duplicate identities are left untouched for the production
        // comparison to reject against the verified current QA plugin set.
        if (!HasValidQa(saved) || !HasValidQa(current)) return;
        saved = Normalize(saved);
        current = Normalize(current);
    }

    static JArray Normalize(JArray entries)
    {
        var result = (JArray)entries.DeepClone();
        var qa = (JObject)result.Single(e => (string)e["guid"] == QaId);
        qa.Remove("module");
        qa.Remove("sha256");
        return result;
    }

    // Only synthetic JSON values and the real production comparator; no files/world mutation.
    // Can run before installation: explicitly normalize local clones then invoke the comparator.
    public static string RunPureSelfChecks()
    {
        Require(Match != null, "fingerprint comparison contract");
        int count = 0;
        Action<bool, string> test = (ok, name) => { if (!ok) throw new Exception("QA fingerprint self-check failed: " + name); count++; };
        Func<JArray, JArray, bool> compare = (a, b) => {
            NormalizePrefix(ref a, ref b);
            return (bool)Match.Invoke(null, new object[] { a, b });
        };
        Func<string, string, JObject> record = (id, version) => new JObject {
            ["guid"] = id, ["version"] = version, ["module"] = "old-module", ["sha256"] = "old-hash"
        };
        var saved = new JArray(record(DialogueSavePlugin.Id, DialogueSavePlugin.Version), record(QaId, QaVersion));
        var current = (JArray)saved.DeepClone();
        current[0]["module"] = "new-own-module"; current[0]["sha256"] = "new-own-hash";
        current[1]["module"] = "new-qa-module"; current[1]["sha256"] = "new-qa-hash";
        var untouched = saved.DeepClone();
        test(compare(saved, current), "own and QA rebuild accepted");
        test(JToken.DeepEquals(saved, untouched), "saved input remains unchanged");
        var wrongVersion = (JArray)saved.DeepClone(); wrongVersion[1]["version"] = "0.2.0";
        test(!compare(wrongVersion, current), "QA version mismatch rejected");
        var duplicate = (JArray)saved.DeepClone(); duplicate.Add(duplicate[1].DeepClone());
        test(!compare(duplicate, current), "duplicate QA identity rejected");
        var missing = (JArray)saved.DeepClone(); missing.RemoveAt(1);
        test(!compare(missing, current), "missing QA identity rejected");
        var ownVersion = (JArray)saved.DeepClone(); ownVersion[0]["version"] = "0.2.0";
        test(!compare(ownVersion, current), "own plugin version mismatch rejected");
        var thirdOld = (JArray)saved.DeepClone(); thirdOld.Add(record("third.party", "1.0.0"));
        var thirdNew = (JArray)current.DeepClone(); thirdNew.Add(record("third.party", "1.0.0"));
        thirdNew[2]["sha256"] = "changed-third-party";
        test(!compare(thirdOld, thirdNew), "third-party same-version hash mismatch rejected");
        thirdNew[2]["sha256"] = "old-hash"; thirdNew[2]["module"] = "changed-third-module";
        test(!compare(thirdOld, thirdNew), "third-party module mismatch rejected");
        return "QA_FINGERPRINT_SELF_CHECKS_OK " + count;
    }
}
