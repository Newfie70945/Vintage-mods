using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!,dll=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(c,n)=>{foreach(string root in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetDirectoryName(dll)!}){string p=Path.Combine(root,n.Name+".dll");if(File.Exists(p))return c.LoadFromAssemblyPath(p);}return null;};
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VintagestoryLib.dll"));
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
Type T(string n)=>assembly.GetType(n,true)!;
object? Default(Type t)=>t==typeof(void)?null:t.IsValueType?Activator.CreateInstance(t):null;
object Proxy(Type t,Func<MethodInfo,object?[]?,object?> f){object p=DispatchProxy.Create(t,typeof(AssetProxy));((AssetProxy)p).Handler=f;return p;}
object? Call(object o,string n,params object?[] a)=>o.GetType().GetMethod(n)!.Invoke(o,a);
void Check(bool b,string s){if(!b)throw new Exception(s);}
object? Get(object o,string n)=>o.GetType().GetProperty(n)!.GetValue(o);
void Set(object o,string n,object? v)=>o.GetType().GetProperty(n)!.SetValue(o,v);
string temp=Path.Combine(Path.GetTempPath(),"nss-generator-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
try{
var serviceType=T("BlockLogger.Services.CustomPatchService");Type apiType=serviceType.GetConstructors().Single().GetParameters()[0].ParameterType;
Type managerType=apiType.GetInterfaces().Append(apiType).Select(t=>t.GetProperty("Assets")).First(p=>p!=null)!.PropertyType,mapType=managerType.GetProperty("AllAssets")!.PropertyType;
var types=mapType.GenericTypeArguments;var assets=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(types))!;
void Asset(string location,string text){var key=Activator.CreateInstance(types[0],location)!;assets.Add(key,Proxy(types[1],(m,a)=>m.Name=="get_Data"?Encoding.UTF8.GetBytes(text):Default(m.ReturnType)));}
Asset("game:recipes/grid/tool/pickaxe.json",File.ReadAllText(Path.Combine(game,"assets/survival/recipes/grid/tool/pickaxe.json")));
Asset("game:itemtypes/tool/pickaxe.json",File.ReadAllText(Path.Combine(game,"assets/survival/itemtypes/tool/pickaxe.json")));
Asset("fixture:recipes/grid/array.json","[{ingredients:{'A/B~':{code:'game:stick'}},output:{code:'first',quantity:1}},{ingredients:{A:{code:'game:stone'}},output:{code:'second',quantity:2}}]");
Asset("fixture:blocktypes/stone.json","{code:'stone',resistance:4,attributes:{'a/b~':7}}");
Asset("fixture:blocktypes/large.json","{variants:["+string.Join(',',Enumerable.Repeat("1",3000))+"],resistance:8}");
bool fail=false;
object api=Proxy(apiType,(m,a)=>m.Name switch{
"get_Assets"=>Proxy(managerType,(mm,aa)=>mm.Name=="get_AllAssets"?assets:Default(mm.ReturnType)),
"get_Logger"=>Proxy(m.ReturnType,(mm,aa)=>Default(mm.ReturnType)),
"GetOrCreateDataPath"=>Path.Combine(temp,(string)a![0]!),
"StoreModConfig"=>fail?throw new IOException("injected persistence failure"):null,
_=>Default(m.ReturnType)});
object service=Activator.CreateInstance(serviceType,api)!;
object Request(string file,string preset,string root="",int quantity=1){object r=Activator.CreateInstance(T("BlockLogger.Services.ContentPatchGenerateRequest"))!;Set(r,"Guided",true);Set(r,"Name","fixture balance");Set(r,"File",file);Set(r,"Preset",preset);Set(r,"RootPath",root);Set(r,"Quantity",quantity);return r;}
var inspected=Call(service,"InspectContentAsset","fixture:recipes/grid/array.json")!;
Check(((IList)Get(inspected,"Roots")!).Count==2,"Array recipe roots not discovered");
var real=Call(service,"InspectContentAsset","game:itemtypes/tool/pickaxe.json")!;
Check(((IList)Get(real,"Fields")!).Count>20,"Real unquoted/commented item asset could not be inspected");
var large=Call(service,"InspectContentAsset","fixture:blocktypes/large.json")!;
Check(((IList)Get(large,"Fields")!).Cast<object>().Any(f=>(string)Get(f,"Path")! == "/resistance"),"Large arrays hid a shallow balance field");
var req=Request("fixture:recipes/grid/array.json","recipe-output-quantity","/1",5);
var generated=Call(service,"GenerateContentPatch",req)!;Check((bool)Get(generated,"Success")!,"Array recipe generate failed");
var definition=Get(generated,"Generated")!;Check((string)Get(definition,"Path")! =="/1/output/quantity","Array recipe pointer lost selection");
var preview=Get(generated,"Preview")!;Check((string)Get(preview,"BeforeJson")! =="2"&&(string)Get(preview,"AfterJson")! =="5","Preview did not show changed value");
var escaped=Request("fixture:recipes/grid/array.json","recipe-ingredient-quantity","/0",3);Set(escaped,"IngredientKey","A/B~");
var escapeResult=Call(service,"GenerateContentPatch",escaped)!;Check((bool)Get(escapeResult,"Success")!&&(string)Get(Get(escapeResult,"Generated")!,"Path")! =="/0/ingredients/A~1B~0/quantity","Ingredient pointer escaping failed");
var property=Request("fixture:blocktypes/stone.json","property");Set(property,"Path","/attributes/a~1b~0");Set(property,"Operation","replace");Set(property,"ValueJson","9");
Check((bool)Get(Call(service,"GenerateContentPatch",property)!,"Success")!,"Escaped block property could not be edited");
Set(property,"ValueJson","");Check(!(bool)Get(Call(service,"GenerateContentPatch",property)!,"Success")!,"Empty number became a null patch");Set(property,"ValueJson","9");
Set(property,"Path","/missing/nested");Check(!(bool)Get(Call(service,"GenerateContentPatch",property)!,"Success")!,"Absent parent accepted");
Set(property,"File","game:../../secret.json");Check(!(bool)Get(Call(service,"GenerateContentPatch",property)!,"Success")!,"Non-asset path accepted");
Set(req,"Quantity",0);Check(!(bool)Get(Call(service,"GenerateContentPatch",req)!,"Success")!,"Invalid quantity accepted");Set(req,"Quantity",5);
Set(req,"Activate",true);var saved=Call(service,"SaveGeneratedContentPatch",req)!;Check((bool)Get(saved,"Success")!,"Stage failed");
string packPath=Path.Combine(temp,"Mods/NewfiesSuiteContentPatches/assets/newfiessuitecontentpatches/patches/generated.json");
byte[] previous=File.ReadAllBytes(packPath);
using(var json=JsonDocument.Parse(previous)){Check(json.RootElement.GetArrayLength()==1&&json.RootElement[0].GetProperty("path").GetString()=="/1/output/quantity","Staged native patch is invalid");}
byte[] zip=(byte[])Call(service,"ExportContentPatchMod")!;using(var archive=new ZipArchive(new MemoryStream(zip))){Check(archive.GetEntry("modinfo.json")!=null&&archive.Entries.Count==2,"Exported patch pack layout invalid");}
fail=true;Set(req,"Quantity",6);var failed=Call(service,"SaveGeneratedContentPatch",req)!;fail=false;
Check(!(bool)Get(failed,"Success")!&&File.ReadAllBytes(packPath).SequenceEqual(previous),"Failed save changed active patch pack");
var snapshot=Call(service,"GetContentSnapshot")!;Check(((IList)Get(snapshot,"Patches")!).Count==1,"Failed save retained an extra definition");
var item=Request("game:itemtypes/tool/pickaxe.json","item-stacksize","",16);Set(item,"Activate",true);Set(item,"Side","");
Check((bool)Get(Call(service,"SaveGeneratedContentPatch",item)!,"Success")!,"Item stack patch failed");
using(var info=JsonDocument.Parse(File.ReadAllText(Path.Combine(temp,"Mods/NewfiesSuiteContentPatches/modinfo.json"))))Check(info.RootElement.GetProperty("requiredOnClient").GetBoolean(),"Client content requirements omitted");
Check(!Directory.EnumerateFiles(temp,"*.tmp",SearchOption.AllDirectories).Any(),"Atomic write temporary files leaked");
Console.WriteLine("PASS: real VS asset syntax, array recipe selection, ingredient/property pointer escaping, before/after previews, invalid paths/quantities, staged native JSON, ZIP export, failed-save rollback and client requirements.");
}finally{Directory.Delete(temp,true);}
public class AssetProxy:DispatchProxy{public Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);}
