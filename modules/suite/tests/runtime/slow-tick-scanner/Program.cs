using System.Reflection;
using System.Runtime.Loader;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(ctx,name)=>{
 foreach(var root in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){
  var p=Path.Combine(root,name.Name+".dll");if(File.Exists(p))return ctx.LoadFromAssemblyPath(p);
 }return null;
};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var scannerType=asm.GetType("BlockLogger.WebDashboard.SlowTickLogScanner",true)!;
void Check(bool condition,string detail){if(!condition)throw new Exception(detail);}
object Scanner(string path,long offset)=>Activator.CreateInstance(scannerType,new object[]{path,offset})!;
void Feed(object s,string text)=>scannerType.GetMethod("Feed")!.Invoke(s,new object[]{text});
void Poll(object s)=>scannerType.GetMethod("Poll")!.Invoke(s,new object[]{512*1024});
string Text(object s)=>(string)scannerType.GetProperty("Text")!.GetValue(s)!;
int Blocks(object s)=>(int)scannerType.GetProperty("BlocksFound")!.GetValue(s)!;

// Only the slow-tick entry and its continuation lines are kept.
var s1=Scanner("",0);
Feed(s1,string.Join("\n",
 "12.5.2024 14:03:21 [Notification] Player joined",
 "12.5.2024 14:03:22 [Warning] A tick took 187 ms",
 "    Breakdown line 1",
 "    Breakdown line 2",
 "12.5.2024 14:03:23 [Notification] Something else entirely",
 "stray continuation of the unrelated entry",
 ""));
Check(Blocks(s1)==1,"One slow-tick entry");
Check(Text(s1).Contains("187 ms")&&Text(s1).Contains("Breakdown line 2"),"Entry and its breakdown are kept");
Check(!Text(s1).Contains("Player joined")&&!Text(s1).Contains("Something else")&&!Text(s1).Contains("stray continuation"),"Unrelated lines are dropped");

// Windows line endings, case-insensitive match, ISO timestamps ending a block.
var s2=Scanner("",0);
Feed(s2,"2024-05-12 14:03:22 [Warning] A TICK TOOK 90 ms\r\n  detail\r\n2024-05-12 14:03:23 [Warning] a tick took 120 ms\r\n2024-05-12 14:03:24 [Notification] done\r\n  not part of a slow tick\r\n");
Check(Blocks(s2)==2,"Two entries across CRLF and ISO timestamps");
Check(Text(s2).Contains("detail")&&!Text(s2).Contains("not part of a slow tick"),"Block ends at the next timestamped entry");

// File polling: starts at the given offset, waits for complete lines, follows rotation.
string dir=Path.Combine(Path.GetTempPath(),"nss-slowtick-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
try{
 string log=Path.Combine(dir,"server-main.log");
 File.WriteAllText(log,"12.5.2024 14:00:00 [Warning] A tick took 999 ms (before logging was turned on)\n");
 var s3=Scanner(log,new FileInfo(log).Length);
 Poll(s3);Check(Blocks(s3)==0,"Entries before the start offset are ignored");
 File.AppendAllText(log,"12.5.2024 14:03:22 [Warning] A tick took 150 ms\n  detail\n12.5.2024 14:03:23 [Warning] A tick too");
 Poll(s3);Check(Blocks(s3)==1&&Text(s3).Contains("detail"),"Complete lines are consumed");
 Check(!Text(s3).Contains("A tick too\n"),"A partly written line is not consumed yet");
 File.AppendAllText(log,"k 90 ms\n");
 Poll(s3);Check(Blocks(s3)==2&&Text(s3).Contains("90 ms"),"The completed line is read on the next poll");
 Poll(s3);Check(Blocks(s3)==2,"Polling again without new data changes nothing");
 File.WriteAllText(log,"12.5.2024 15:00:00 [Warning] A tick took 210 ms\n");
 Poll(s3);Check(Blocks(s3)==3&&Text(s3).Contains("210 ms"),"A rotated or truncated log is read from the start");
 var s4=Scanner(log,0);Poll(s4);Check(Blocks(s4)==1,"Offset zero reads the whole file");
 Poll(Scanner(Path.Combine(dir,"missing.log"),0));
}finally{try{Directory.Delete(dir,true);}catch{}}

// Retained text is capped to the newest entries.
var s5=Scanner("",0);
for(int i=0;i<3000;i++)Feed(s5,$"12.5.2024 14:03:22 [Warning] A tick took {i} ms {new string('x',80)}\n");
Check(Blocks(s5)==3000,"Every entry is counted");
Check(Text(s5).Length<=24000,"Retained text is bounded");
Check(Text(s5).Contains("took 2999 ms")&&!Text(s5).Contains("took 0 ms"),"The newest entries are the ones kept");

Console.WriteLine("PASS: slow-tick entry extraction, partial-line polling, log rotation and bounded retention against shipping assembly");
