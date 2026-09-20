using System; using System.IO; using System.Reflection; using System.Runtime.Loader; using System.Runtime.CompilerServices; using System.Diagnostics; using Newtonsoft.Json; using StudentAgeDialogueSave.GameIntegration;
class Program {
 static void Main(string[] args){ AssemblyLoadContext.Default.Resolving += (c,n) => {string p=Path.Combine(args[0],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;}; Run(); }
 [MethodImpl(MethodImplOptions.NoInlining)]static void Run(){var text=typeof(Config.Cfg).GetProperty("TextCfgMap");text.SetValue(null,Activator.CreateInstance(text.PropertyType));var timer=Stopwatch.StartNew();ConfigFingerprintSnapshot.WarmPlans(JsonSerializer.Create(new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MetadataPropertyHandling=MetadataPropertyHandling.Ignore,MaxDepth=64}));Console.WriteLine("ACTUAL_CONFIG_SCHEMA_PASS "+timer.ElapsedMilliseconds+"ms");}
}
