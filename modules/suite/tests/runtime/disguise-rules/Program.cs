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
var rules=asm.GetType("BlockLogger.Admin.DisguiseRules",true)!;
void Check(bool c,string d){if(!c)throw new Exception(d);}
string Normalize(string? s)=>(string)rules.GetMethod("NormalizeCode")!.Invoke(null,[s])!;
bool Forbidden(string s)=>(bool)rules.GetMethod("IsForbidden")!.Invoke(null,[s])!;

Check(Normalize("cow")=="game:cow"&&Normalize(" Chicken-Hen ")=="game:chicken-hen","bare names get the game domain, case and spaces tidied");
Check(Normalize("mymod:dragon")=="mymod:dragon","an explicit domain is kept");
Check(Normalize("")==""&&Normalize(null)==""&&Normalize("a b")==""&&Normalize("a:b:c")==""&&Normalize(":x")==""&&Normalize("x:")=="","unusable input becomes empty");
Check(Normalize(new string('a',101))=="","over-long input is rejected");
Check(Forbidden("game:player")&&Forbidden("game:player-x")&&!Forbidden("game:chicken-hen"),"players are never a disguise");

var codes=new List<string>{"game:chicken-hen","game:chicken-rooster","game:chicken-baby","game:cow","game:player","game:drifter-normal"};
var found=(List<string>)rules.GetMethod("Search")!.Invoke(null,[codes,"chick",10])!;
Check(found.Count==3&&found[0]=="game:chicken-baby"&&!found.Contains("game:player"),"search is case-insensitive, shortest first, never lists players: "+string.Join(",",found));
Check(((List<string>)rules.GetMethod("Search")!.Invoke(null,[codes,"",2])!).Count==2,"limit is honoured");

string Resolve(string input,out List<string> candidates){var args=new object?[]{codes,input,null};var r=(string)rules.GetMethod("Resolve")!.Invoke(null,args)!;candidates=(List<string>)args[2]!;return r;}
Check(Resolve("cow",out _)=="game:cow","an exact code wins");
Check(Resolve("drifter",out _)=="game:drifter-normal","a single partial match is accepted");
Check(Resolve("chicken",out var many)==""&&many.Count==3,"an ambiguous name is reported, not guessed");
Check(Resolve("player",out var none)==""&&none.Count==0,"player is not offered");
Check(Resolve("zzz",out _)=="","unknown names resolve to nothing");
Console.WriteLine("PASS: disguise code normalisation, forbidden types, search and resolution");
