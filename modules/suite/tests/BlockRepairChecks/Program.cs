using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Text;
using System.Buffers.Binary;
using BlockLogger.WorldRepair;
using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.API.Datastructures;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,n)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib")}){var p=Path.Combine(folder,n.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
if(args.Length==3&&args[0]=="seed"){Checks.Seed(args[1],args[2]);}else if(args.Length==4&&args[0]=="verify"){Checks.Verify(args[1],args[2],int.Parse(args[3]));}else Checks.Run();
static class Checks
{
 static int count;
 static void Assert(bool value,string why){if(!value)throw new Exception(why);count++;}
 static void Var(Stream s,ulong n){while(n>=128){s.WriteByte((byte)(n|128));n>>=7;}s.WriteByte((byte)n);}
 static byte[] Field(int tag,byte[] bytes){using var s=new MemoryStream();Var(s,(ulong)((tag<<3)|2));Var(s,(ulong)bytes.Length);s.Write(bytes);return s.ToArray();}
 static byte[] Blob(byte[] solid,byte[] fluid,byte[]? decor=null,byte[]? entity=null){using var s=new MemoryStream();s.Write(Field(1,solid));s.Write(Field(16,fluid));s.WriteByte(120);s.WriteByte(2);s.Write(Field(90,Encoding.UTF8.GetBytes("opaque metadata must survive")));if(decor!=null)s.Write(Field(14,decor));if(entity!=null)s.Write(Field(8,entity));return s.ToArray();}
 public static void Verify(string beforePath,string afterPath,int target){
  var before=File.ReadAllBytes(beforePath);var after=File.ReadAllBytes(afterPath);var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)));var codec=new BlockIdChunkCodec(pool);
  Assert(codec.Inspect(before,id=>id==999999)[999999][0]==1,"seeded source present");Assert(codec.Inspect(after,id=>id==999999).Count==0,"repaired source absent");
  var originalFields=BlockIdChunkCodec.Fields(before);var repairedFields=BlockIdChunkCodec.Fields(after);
  foreach(var f in originalFields.Where(x=>x.Tag!=1)){var g=repairedFields.Single(x=>x.Tag==f.Tag);Assert(before.AsSpan(f.Start,f.End-f.Start).SequenceEqual(after.AsSpan(g.Start,g.End-g.Start)),"untouched field "+f.Tag);}
  var layers=new List<ChunkDataLayer>();foreach(var blob in new[]{before,after}){var f=BlockIdChunkCodec.Fields(blob).Single(x=>x.Tag==1);var l=new ChunkDataLayer(pool);typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(l,[blob.AsSpan(f.Data,f.Length).ToArray()]);layers.Add(l);}
  for(int i=0;i<32768;i++)Assert(layers[1].GetUnsafe(i)==(i==17?target:layers[0].GetUnsafe(i)),"cell preserved "+i);
  Console.WriteLine($"{count} end-to-end byte/cell preservation assertions passed.");
 }
 public static void Seed(string input,string output){
  var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)));var blob=File.ReadAllBytes(input);var fields=BlockIdChunkCodec.Fields(blob);var solidField=fields.Single(x=>x.Tag==1);var layer=new BlockChunkDataLayer(pool);
  typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(layer,[blob.AsSpan(solidField.Data,solidField.Length).ToArray()]);layer.Set(17,999999);var compressed=ChunkDataLayer.Compress(layer,new int[15360]);
  using var target=new MemoryStream();foreach(var f in fields){if(f.Tag==1)target.Write(Field(1,compressed));else target.Write(blob,f.Start,f.End-f.Start);}File.WriteAllBytes(output,target.ToArray());Console.WriteLine("Seeded one out-of-range numeric block ID; untouched fields preserved.");
 }
 public static void Run(){
  var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain))){CacheSize=16};var codec=new BlockIdChunkCodec(pool);var scratch=new int[15360];
  var solid=new BlockChunkDataLayer(pool);solid.PopulateWithAir();solid.Set(17,123456);solid.Set(32767,123456);solid.Set(15,42);
  var fluid=new BlockChunkDataLayer(pool);fluid.PopulateWithAir();fluid.Set(23,123456);
  var decor=new byte[16];BinaryPrimitives.WriteInt32LittleEndian(decor.AsSpan(4),123456);BinaryPrimitives.WriteInt32LittleEndian(decor.AsSpan(8),17);BinaryPrimitives.WriteInt32LittleEndian(decor.AsSpan(12),42);
  var blob=Blob(ChunkDataLayer.Compress(solid,scratch),ChunkDataLayer.Compress(fluid,scratch),decor);
  var found=codec.Inspect(blob,id=>id==123456);Assert(found[123456].SequenceEqual(new long[]{2,1,1}),"exact counts in all three layers");
  var fixedBlob=codec.Replace(blob,123456,42,out long changed);Assert(changed==4,"all matching occurrences replaced");Assert(codec.Inspect(fixedBlob,id=>id==123456).Count==0,"old ID absent");Assert(codec.Inspect(fixedBlob,id=>id==42)[42].SequenceEqual(new long[]{3,1,2}),"destination occurrences and unrelated positions preserved");
  var oldField=BlockIdChunkCodec.Fields(blob).Single(x=>x.Tag==90);var newField=BlockIdChunkCodec.Fields(fixedBlob).Single(x=>x.Tag==90);Assert(blob.AsSpan(oldField.Start,oldField.End-oldField.Start).SequenceEqual(fixedBlob.AsSpan(newField.Start,newField.End-newField.Start)),"unknown protobuf field byte-exact preservation");
  Assert(ReferenceEquals(blob,codec.Replace(blob,999,42,out changed))&&changed==0,"no-op returns original bytes");
  var removed=codec.Replace(blob,123456,0,out changed);Assert(changed==4&&BlockIdChunkCodec.Fields(removed).Single(x=>x.Tag==14).Length==8,"air removes matching decor pairs and terrain IDs");
  using var be=new MemoryStream();using(var writer=new BinaryWriter(be,Encoding.UTF8,true)){writer.Write("Chest");var tree=new TreeAttribute();tree.SetInt("posx",17);tree.SetInt("posy",0);tree.SetInt("posz",0);tree.ToBytes(writer);}
  var protectedBlob=Blob(ChunkDataLayer.Compress(solid,scratch),ChunkDataLayer.Compress(fluid,scratch),entity:be.ToArray());
  try{codec.Replace(protectedBlob,123456,42,out _);throw new Exception("Missing inventory guard");}catch(InvalidOperationException){Assert(true,"source block entity prevents destructive replacement");}
  try{codec.Replace(blob,0,42,out _);throw new Exception("Missing air-source guard");}catch(ArgumentException){Assert(true,"air source refused");}
  foreach(var bad in new[]{new byte[]{0},new byte[]{10,255},new byte[]{11},new byte[]{128,128,128,128,128,128,128,128,128,128,128}}){try{BlockIdChunkCodec.Fields(bad);throw new Exception("Missing malformed data guard");}catch(InvalidDataException){count++;}}
  using var duplicate=new MemoryStream();duplicate.Write(blob);duplicate.Write(Field(1,[]));try{codec.Inspect(duplicate.ToArray(),_=>true);throw new Exception("Missing duplicate guard");}catch(InvalidDataException){count++;}
  for(int round=0;round<30;round++){int old=100000+round;var layer=new BlockChunkDataLayer(pool);layer.PopulateWithAir();for(int i=0;i<100;i++)layer.Set((i*97)%32768,old);var b=Blob(ChunkDataLayer.Compress(layer,scratch),new byte[4]);var c=codec.Replace(b,old,42,out long n);Assert(n==100&&codec.Inspect(c,id=>id==42)[42][0]==100,"palette round trip "+round);pool.FreeArrays(layer);}
  pool.FreeAll();Console.WriteLine($"{count} block-ID codec and preservation checks passed against official compression.");
 }
}
