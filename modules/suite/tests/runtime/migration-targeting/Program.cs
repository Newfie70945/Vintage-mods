using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
string game=Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")!);
string path=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(string root in new[]{Path.GetDirectoryName(path)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){
  string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);
 }return null;
};
var mod=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var api=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VintagestoryAPI.dll"));
var lib=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VintagestoryLib.dll"));
Type T(string name)=>api.GetType(name)??lib.GetType(name)??mod.GetType(name)??throw new Exception(name);
object Proxy(Type type,Func<MethodInfo,object?[]?,object?> handler){var p=DispatchProxy.Create(type,typeof(ApiProxy));((ApiProxy)p).Handler=handler;return p;}
object? Default(Type type)=>type==typeof(void)?null:type.IsValueType?Activator.CreateInstance(type):null;
object MakePos(int x,int y,int z,int dimension)=>Activator.CreateInstance(T("Vintagestory.API.MathTools.BlockPos"),x,y,z,dimension)!;
object? Read(object target,string name)=>target.GetType().GetProperty(name)?.GetValue(target)??target.GetType().GetField(name)?.GetValue(target);
void Write(object target,string name,object value){var p=target.GetType().GetProperty(name);if(p!=null)p.SetValue(target,value);else target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.SetValue(target,value);}
int count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
var serviceType=T("BlockLogger.WorldRepair.BlockIdRepairService");var service=RuntimeHelpers.GetUninitializedObject(serviceType);
var method=serviceType.GetMethod("MigrationTargetPosition",BindingFlags.NonPublic|BindingFlags.Instance)!;
var selection=Activator.CreateInstance(T("Vintagestory.API.Common.BlockSelection"))!;Write(selection,"Position",MakePos(12,65,34,2));
var player=RuntimeHelpers.GetUninitializedObject(T("Vintagestory.Server.ServerPlayer"));
var data=RuntimeHelpers.GetUninitializedObject(T("Vintagestory.Server.ServerWorldPlayerData"));
var entity=RuntimeHelpers.GetUninitializedObject(T("Vintagestory.API.Common.EntityPlayer"));
Write(entity,"BlockSelection",selection);Write(data,"EntityPlayer",entity);Write(player,"worlddata",data);
object Target(params string[] tokens)=>method.Invoke(service,[player,tokens])!;
void Refused(string expected,params string[] tokens){try{Target(tokens);throw new Exception("Unsafe target accepted");}catch(TargetInvocationException e){Check(e.InnerException!.Message.Contains(expected),e.InnerException.Message);}}
var target=Target();Check((int)Read(target,"dimension")! ==2,"Selected dimension lost");Check(!ReferenceEquals(Read(selection,"Position"),target),"Selection position not copied");
Refused("dimension=N accompanies", "dimension=5");
var core=Proxy(T("Vintagestory.API.Server.ICoreServerAPI"),(m,a)=>m.Name=="get_World"?Proxy(m.ReturnType,(wm,wa)=>wm.Name=="get_BlockAccessor"?Proxy(wm.ReturnType,(bm,ba)=>bm.Name=="GetChunkAtBlockPos"?Proxy(bm.ReturnType,(cm,ca)=>Default(cm.ReturnType)):throw new Exception("Unexpected block access "+bm.Name)):Default(wm.ReturnType)):Default(m.ReturnType));
serviceType.GetField("api",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,core);
Write(entity,"BlockSelection",null!);target=Target("at=20,70,30","dimension=7");Check((int)Read(target,"dimension")! ==7,"Explicit dimension ignored");
Refused("between 0 and 511","at=20,70,30","dimension=999");Refused("Player dimension is unavailable","at=20,70,30");
// Check emitted field operands, including unreachable branches: JIT resolution
// can fail on a missing member before the selected-block branch executes.
var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>(ushort)o.Value);
byte[] il=method.GetMethodBody()!.GetILAsByteArray()!;
for(int i=0;i<il.Length;){ushort value=il[i++];if(value==0xfe)value=(ushort)(0xfe00|il[i++]);var op=ops[value];int size=op.OperandType switch{OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4};if(op.OperandType==OperandType.InlineField){var field=method.Module.ResolveField(BitConverter.ToInt32(il,i))!;Check(!(field.Name=="Pos"&&field.DeclaringType?.FullName=="Vintagestory.API.Common.Entities.Entity"),"Direct Entity.Pos field token remains");}i+=size;}
Console.WriteLine($"PASS {count} shipping-method targeting/IL checks: selection, explicit dimension, invalid dimensions, unavailable fallback and no direct Entity.Pos field dependency.");
public class ApiProxy:DispatchProxy{public Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);}
