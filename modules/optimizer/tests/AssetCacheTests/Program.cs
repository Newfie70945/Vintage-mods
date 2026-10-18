using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.Common;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(string folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Tests.Run(args);
static class Tests
{
 static int checks;
 static Func<byte[],int,byte[]> Compress=null!;
 static void Check(bool result,string reason){if(!result)throw new Exception(reason);checks++;}
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run(string[] args)
 {
  Check(AssetCompressionOptimizations.ValidateContract(out var why),why);
  var harmony=new Harmony("test.nos.asset-cache");
  byte[] source=new byte[131072];new Random(42).NextBytes(source);for(int i=0;i<source.Length;i++)if(i%4!=0)source[i]=0;
  Compress=AssetCompressionOptimizations.Target.CreateDelegate<Func<byte[],int,byte[]>>();
  byte[] expected=Compress(source,source.Length);
  harmony.Patch(AssetCompressionOptimizations.Target,new HarmonyMethod(AssetCompressionOptimizations.PrefixMethod),new HarmonyMethod(AssetCompressionOptimizations.PostfixMethod));
  try {
   AssetCompressionOptimizations.Configure(true);AssetCompressionOptimizations.Track(source,1048576);
   byte[] first=Compress(source,source.Length),second=Compress(source,source.Length);
   Check(first.SequenceEqual(expected)&&second.SequenceEqual(expected),"cold/hit payload identical to official compression");
   Check(AssetCompressionOptimizations.Counters.Hits>0,"actual cache-hit path exercised");
   Check(!ReferenceEquals(first,second),"independent packet bytes");second[0]=255;
   Check(Compress(source,source.Length).SequenceEqual(expected),"socket header mutation cannot corrupt cache");
   source[100]^=1;var changed=Compress(source,source.Length);
   Check(!changed.SequenceEqual(expected),"same buffer and length with changed contents invalidates");
   Check(Compression.Decompress(changed,4,changed.Length-4).SequenceEqual(source),"changed payload round-trips");
   int corrupt=0;
   Parallel.For(0,128,i=>{var value=Compress(source,source.Length);if(!Compression.Decompress(value,4,value.Length-4).SequenceEqual(source))Interlocked.Increment(ref corrupt);value[0]=(byte)i;});
   Check(corrupt==0,"parallel callers retain independent correct packets");
   var c=AssetCompressionOptimizations.Counters;
   Compress((byte[])source.Clone(),source.Length);
   Check(AssetCompressionOptimizations.Counters.Hits==c.Hits&&AssetCompressionOptimizations.Counters.Misses==c.Misses,"untracked packets untouched");
   byte[] oversized=new byte[1048577];AssetCompressionOptimizations.Track(oversized,1048576);Check(AssetCompressionOptimizations.Counters.Bytes==0,"oversized buffers release cache");
   AssetCompressionOptimizations.Track(source,1048576);
   for(int len=1461;len<source.Length;len+=12347){var value=Compress(source,len);Check(Compression.Decompress(value,4,value.Length-4).SequenceEqual(source.Take(len)),"length changes roundtrip");}
   var noise=new byte[131072];new Random(99).NextBytes(noise);AssetCompressionOptimizations.Track(noise,1048576);
   Compress(noise,noise.Length);var rejected=AssetCompressionOptimizations.Counters;Compress(noise,noise.Length);
   Check(AssetCompressionOptimizations.Counters.Bytes==0&&AssetCompressionOptimizations.Counters.Misses==rejected.Misses,"low-compressibility assets bypass repeated cache probes");
   AssetCompressionOptimizations.Configure(false);Check(AssetCompressionOptimizations.Counters.Bytes==0,"disable releases buffers");
   c=AssetCompressionOptimizations.Counters;Compress(source,source.Length);Check(AssetCompressionOptimizations.Counters.Misses==c.Misses,"disabled prefix uses stock");
   if(args.Contains("--benchmark")) {
    Benchmark(source,"sparse-bytes-128KiB");
    var text=System.Text.Encoding.UTF8.GetBytes("{\"code\":\"example:recipe\",\"ingredients\":[\"game:stone\",\"game:stick\"]}\n");
    var assets=new byte[1048576];for(int i=0;i<assets.Length;i++)assets[i]=text[i%text.Length];
    Benchmark(assets,"repeated-json-1MiB");
   }
   Console.WriteLine($"{checks} asset compression checks passed against official compression and actual Harmony targets.");
  } finally {AssetCompressionOptimizations.Configure(false);harmony.UnpatchAll(harmony.Id);}
 }
 static void Benchmark(byte[] source,string scenario)
 {
  const int count=4000;
  double Measure(bool enabled){AssetCompressionOptimizations.Configure(enabled);AssetCompressionOptimizations.Track(source,4194304);for(int i=0;i<20;i++)Compress(source,source.Length);var watch=Stopwatch.StartNew();for(int i=0;i<count;i++)Compress(source,source.Length);return watch.Elapsed.TotalMilliseconds/count;}
  var rows=new List<object>();for(int i=0;i<5;i++){double stock,cached;if(i%2==0){stock=Measure(false);cached=Measure(true);}else{cached=Measure(true);stock=Measure(false);}rows.Add(new{scenario,trial=i,bytes=source.Length,stockMs=stock,cachedMs=cached});}
  Console.WriteLine(JsonSerializer.Serialize(rows));
 }
}
