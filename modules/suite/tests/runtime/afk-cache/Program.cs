using System.Reflection;
using System.Runtime.Loader;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!,dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(c,n)=>{foreach(var root in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string p=Path.Combine(root,n.Name+".dll");if(File.Exists(p))return c.LoadFromAssemblyPath(p);}return null;};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var type=assembly.GetType("BlockLogger.Afk.NewfiesAfkModSystem",true)!;
var get=type.GetMethod("GetMemberValue",BindingFlags.Static|BindingFlags.NonPublic)!;
var set=type.GetMethod("SetMemberIfExists",BindingFlags.Static|BindingFlags.NonPublic)!;
object? Read(object target,string name)=>get.Invoke(null,new object[]{target,name});
bool Write(object target,string name,object value)=>(bool)set.Invoke(null,new object[]{target,name,value})!;
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
var target=new Child();Check((int)Read(target,"Inherited")! ==3,"private inherited field");Check((int)Read(target,"Value")! ==4,"property lookup");
Check(Write(target,"Value",9)&&(int)Read(target,"Value")! ==9,"cache reads live property values");
Check(Write(target,"Speed",2.5)&&(float)Read(target,"Speed")! ==2.5f,"field value conversion");
for(int i=0;i<100;i++)Check(Read(target,"Missing")==null&&!Write(target,"Missing",1),"missing member remains safe");
Check((int)Read(new Other(),"Value")! ==17,"cache is separated by runtime type");
Console.WriteLine("PASS: AFK cached inherited/property/field reads, live writes/conversion, repeated misses and type isolation");
class Parent{private readonly int Inherited=3;public int Read()=>Inherited;}
class Child:Parent{public int Value{get;set;}=4;public float Speed=0;}
class Other{public int Value=>17;}
