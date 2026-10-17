using Mono.Cecil;
using Mono.Cecil.Cil;
string source=Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")??throw new Exception("Set VINTAGE_STORY."));
string target=Path.GetFullPath(args.ElementAtOrDefault(0)??throw new Exception("Pass a new temporary API fixture directory."));
if(Directory.Exists(target))throw new Exception("Fixture output must not exist.");
Directory.CreateDirectory(target);
foreach(string entry in Directory.EnumerateFileSystemEntries(source)) {
 string name=Path.GetFileName(entry), destination=Path.Combine(target,name);
 if(name=="VintagestoryAPI.dll")continue;
 if(File.Exists(destination)||Directory.Exists(destination))continue;
 if(Directory.Exists(entry))Directory.CreateSymbolicLink(destination,entry);else File.CreateSymbolicLink(destination,entry);
}
using var resolver=new DefaultAssemblyResolver();
foreach(string folder in new[]{source,Path.Combine(source,"Lib"),Path.Combine(source,"Mods"),Path.GetDirectoryName(typeof(object).Assembly.Location)!})resolver.AddSearchDirectory(folder);
using var assembly=AssemblyDefinition.ReadAssembly(Path.Combine(source,"VintagestoryAPI.dll"),new ReaderParameters{AssemblyResolver=resolver});
var entity=assembly.MainModule.Types.Single(t=>t.FullName=="Vintagestory.API.Common.Entities.Entity");
var property=entity.Properties.Single(p=>p.Name=="Pos");
Console.WriteLine("Original Pos layout: property");
property.Name="__InteractionFixturePos";
entity.Fields.Add(new FieldDefinition("Pos",FieldAttributes.Public,property.PropertyType));
assembly.Write(Path.Combine(target,"VintagestoryAPI.dll"));
Console.WriteLine("Created field-only Pos member runtime fixture at "+target);

if(args.Length<3)return;
string before=Path.GetFullPath(args[2]);
if(Directory.Exists(before))throw new Exception("Reproduction output must not exist.");
Directory.CreateDirectory(before);
string zip=Path.GetFullPath(args[1]);
System.IO.Compression.ZipFile.ExtractToDirectory(zip,before,true);
using var archive=System.IO.Compression.ZipFile.OpenRead(zip);
using var bytes=new MemoryStream();
using(var entry=archive.GetEntry("NewfiesServerEssentialsSuite.dll")!.Open())entry.CopyTo(bytes);
bytes.Position=0;
using var mod=AssemblyDefinition.ReadAssembly(bytes,new ReaderParameters{AssemblyResolver=resolver});
var validate=mod.MainModule.Types.Single(t=>t.FullName=="BlockLogger.AntiCheat.PacketIngressGuard").Methods.Single(m=>m.Name=="ValidateBlockTarget");
int replaced=0;
foreach(var instruction in validate.Body.Instructions){
 if(instruction.Operand is MethodReference m && m.Name=="get_Pos" && m.DeclaringType.FullName==entity.FullName){
  instruction.OpCode=OpCodes.Ldfld;
  instruction.Operand=new FieldReference("Pos",m.ReturnType,m.DeclaringType);
  replaced++;
 }
}
if(replaced!=2)throw new Exception("Expected two direct Pos calls in prior guard.");
mod.Write(Path.Combine(before,"NewfiesServerEssentialsSuite.dll"));
Console.WriteLine("Created stale-field-binding reproduction binary: "+replaced+" Pos reads.");
