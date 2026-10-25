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
var rules=asm.GetType("BlockLogger.WebMap.WebMapTranslocatorRules",true)!;
void Check(bool c,string d){if(!c)throw new Exception(d);}
object? Call(string name,params object?[] args)=>rules.GetMethod(name)!.Invoke(null,args);
bool Code(string? p)=>(bool)Call("IsTranslocatorCode",p)!;
Check(Code("statictranslocator-normal-north")&&Code("statictranslocator-broken-east")&&Code("translocator-x"),"translocator block codes recognised");
Check(!Code("porterfoo")&&!Code("rock-granite")&&!Code(null)&&!Code(""),"other blocks rejected");
Check((string)Call("StateFor","statictranslocator-broken-north")!=null&&(string)Call("StateFor","statictranslocator-broken-north")!=("active"),"broken pads flagged");
Check((string)Call("StateFor","statictranslocator-normal-north")!=("broken"),"repaired pads active");
Check((string)Call("IdFor",1,2,3)!="" && (string)Call("IdFor",1,2,3)=="1,2,3","stable id");
bool Jump(double dx,double dz,double s)=>(bool)Call("IsTeleportJump",dx,dz,s,20.0,25.0)!;
Check(!Jump(4,3,.5),"walking is not a jump");
Check(!Jump(30,0,1.0),"a fast second of travel is not a jump");
Check(Jump(300,0,.5),"hundreds of blocks in half a second is a jump");
Check(!Jump(60,0,3.0),"long gap between samples (lag) widens the allowance");
Check(Jump(500,0,3.0),"huge jump still detected after a lag gap");
Check(Jump(1000,0,100),"allowance is capped, so an old sample cannot hide a jump");
Check((bool)Call("Near",10,10,10.5,10.5,3.0)!&&!(bool)Call("Near",10,10,20.0,20.0,3.0)!,"near test is horizontal distance");
Console.WriteLine("PASS: translocator block codes, states, ids, teleport-jump detection and proximity");
