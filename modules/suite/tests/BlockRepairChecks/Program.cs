using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Text;
using System.Buffers.Binary;
using BlockLogger.WorldRepair;
using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,n)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib")}){var p=Path.Combine(folder,n.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
if(args.Length==3&&args[0]=="seed-live"){Checks.Seed(args[1],args[2],999999,999998,999997);}else if(args.Length==5&&args[0]=="verify-live"){Checks.VerifyBatch(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]),false,int.Parse(args[3]));}else if(args.Length==5&&args[0]=="verify-batch"){Checks.VerifyBatch(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]));}else if(args.Length==3&&args[0]=="seed-two"){Checks.Seed(args[1],args[2],999999,999998);}else if(args.Length==5&&args[0]=="verify-selected"){Checks.Verify(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]));}else if(args.Length==3&&args[0]=="seed-air"){Checks.Seed(args[1],args[2],0);}else if(args.Length==5&&args[0]=="verify-restore"){Checks.VerifyRestore(args[1],args[2],int.Parse(args[3]),args[4]);}else if(args.Length==5&&args[0]=="seed-restore"){Checks.SeedInventory(args[1],args[2],long.Parse(args[3]),args[4]=="empty");}else if(args.Length==4&&args[0]=="seed-inventory"){Checks.SeedInventory(args[1],args[2],long.Parse(args[3]));}else if(args.Length==3&&args[0]=="seed"){Checks.Seed(args[1],args[2]);}else if(args.Length==4&&args[0]=="verify"){Checks.Verify(args[1],args[2],int.Parse(args[3]));}else {Checks.Run();HistoryChecks.Run();RollbackChatChecks.Run();}
static class Checks
{
 static int count;
 static void Assert(bool value,string why){if(!value)throw new Exception(why);count++;}
 static void Var(Stream s,ulong n){while(n>=128){s.WriteByte((byte)(n|128));n>>=7;}s.WriteByte((byte)n);}
 static byte[] Field(int tag,byte[] bytes){using var s=new MemoryStream();Var(s,(ulong)((tag<<3)|2));Var(s,(ulong)bytes.Length);s.Write(bytes);return s.ToArray();}
 static byte[] Blob(byte[] solid,byte[] fluid,byte[]? decor=null,byte[]? entity=null){using var s=new MemoryStream();s.Write(Field(1,solid));s.Write(Field(16,fluid));s.WriteByte(120);s.WriteByte(2);s.Write(Field(90,Encoding.UTF8.GetBytes("opaque metadata must survive")));if(decor!=null)s.Write(Field(14,decor));if(entity!=null)s.Write(Field(8,entity));return s.ToArray();}
 public static void Verify(string beforePath,string afterPath,int target,int source=999999){
  var before=File.ReadAllBytes(beforePath);var after=File.ReadAllBytes(afterPath);var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)));var codec=new BlockIdChunkCodec(pool);
  if(source!=0){Assert(codec.Inspect(before,id=>id==source)[source][0]==1,"seeded source present");Assert(codec.Inspect(after,id=>id==source).Count==0,"repaired source absent");}else Assert(codec.ReadCells(before,[17])[17][0]==0,"selected air source present");
  var originalFields=BlockIdChunkCodec.Fields(before);var repairedFields=BlockIdChunkCodec.Fields(after);
  foreach(var f in originalFields.Where(x=>x.Tag!=1)){var g=repairedFields.Single(x=>x.Tag==f.Tag);Assert(before.AsSpan(f.Start,f.End-f.Start).SequenceEqual(after.AsSpan(g.Start,g.End-g.Start)),"untouched field "+f.Tag);}
  var layers=new List<ChunkDataLayer>();foreach(var blob in new[]{before,after}){var f=BlockIdChunkCodec.Fields(blob).Single(x=>x.Tag==1);var l=new ChunkDataLayer(pool);typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(l,[blob.AsSpan(f.Data,f.Length).ToArray()]);layers.Add(l);}
  for(int i=0;i<32768;i++)Assert(layers[1].GetUnsafe(i)==(i==17?target:layers[0].GetUnsafe(i)),"cell preserved "+i);
  Console.WriteLine($"{count} end-to-end byte/cell preservation assertions passed.");
 }
 public static void VerifyBatch(string beforePath,string afterPath,int first,int second,bool opaque=true,int? third=null){
  byte[] before=File.ReadAllBytes(beforePath),after=File.ReadAllBytes(afterPath);var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)));var layers=new List<ChunkDataLayer>();
  foreach(var bytes in new[]{before,after}){var f=BlockIdChunkCodec.Fields(bytes).Single(f=>f.Tag==1);var layer=new ChunkDataLayer(pool);typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(layer,[bytes.AsSpan(f.Data,f.Length).ToArray()]);layers.Add(layer);}
  Assert(layers[0].GetUnsafe(17)==999999&&layers[0].GetUnsafe(18)==999998,"two distinct sources seeded");
  for(int i=0;i<32768;i++)Assert(layers[1].GetUnsafe(i)==(i==17?first:i==18?second:third.HasValue&&(i==19||i==20)?third.Value:layers[0].GetUnsafe(i)),"batch cell preservation "+i);
  var fields=BlockIdChunkCodec.Fields(after);foreach(var f in BlockIdChunkCodec.Fields(before).Where(f=>opaque&&f.Tag!=1)){var g=fields.Single(g=>g.Tag==f.Tag);Assert(before.AsSpan(f.Start,f.End-f.Start).SequenceEqual(after.AsSpan(g.Start,g.End-g.Start)),"batch opaque field preservation "+f.Tag);}
  foreach(var layer in layers)pool.FreeArrays(layer);pool.FreeAll();Console.WriteLine($"{count} batch restart cell/byte preservation assertions passed.");
 }
 public static void VerifyRestore(string beforePath,string afterPath,int target,string mode){
  byte[] before=File.ReadAllBytes(beforePath),after=File.ReadAllBytes(afterPath);var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)));var codec=new BlockIdChunkCodec(pool);
  var oldFields=BlockIdChunkCodec.Fields(before);var newFields=BlockIdChunkCodec.Fields(after);
  foreach(var f in oldFields.Where(f=>f.Tag is not (1 or 8))){var g=newFields.Single(g=>g.Tag==f.Tag);Assert(before.AsSpan(f.Start,f.End-f.Start).SequenceEqual(after.AsSpan(g.Start,g.End-g.Start)),"unchanged field "+f.Tag);}
  var layers=new List<ChunkDataLayer>();foreach(var bytes in new[]{before,after}){var f=BlockIdChunkCodec.Fields(bytes).Single(f=>f.Tag==1);var layer=new ChunkDataLayer(pool);typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(layer,[bytes.AsSpan(f.Data,f.Length).ToArray()]);layers.Add(layer);}
  for(int i=0;i<32768;i++)Assert(layers[1].GetUnsafe(i)==(i==17?target:layers[0].GetUnsafe(i)),"single-cell preservation "+i);
  var a=oldFields.Single(f=>f.Tag==8);var b=newFields.Single(f=>f.Tag==8);byte[] oldEntity=before.AsSpan(a.Data,a.Length).ToArray(),newEntity=after.AsSpan(b.Data,b.Length).ToArray();
  var oldReport=SavedBlockEntityInspector.Inspect(oldEntity);var newReport=SavedBlockEntityInspector.Inspect(newEntity);
  Assert(newReport.Complete&&newReport.InventorySlotCount==14,"restored inventory verified");
  if(mode=="retained")Assert(oldEntity.SequenceEqual(newEntity),"retained full attributes byte exact");
  else{Assert(newReport.NonEmptyStacks==1&&newReport.SerializedItemQuantity==1,"snapshot restored one book");Assert(oldEntity.AsSpan(0,oldReport.SlotsOffset).SequenceEqual(newEntity.AsSpan(0,newReport.SlotsOffset)),"entity prefix unchanged");Assert(oldEntity.AsSpan(oldReport.SlotsOffset+oldReport.SlotsLength).SequenceEqual(newEntity.AsSpan(newReport.SlotsOffset+newReport.SlotsLength)),"entity suffix unchanged");}
  Console.WriteLine($"{count} end-to-end inventory restoration assertions passed.");
 }
 public static void Seed(string input,string output,int source=999999,int? second=null,int? third=null){
  var pool=new ChunkDataPool(32,(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)));var blob=File.ReadAllBytes(input);var fields=BlockIdChunkCodec.Fields(blob);var solidField=fields.Single(x=>x.Tag==1);var layer=new BlockChunkDataLayer(pool);
  typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(layer,[blob.AsSpan(solidField.Data,solidField.Length).ToArray()]);layer.Set(17,source);if(second.HasValue)layer.Set(18,second.Value);if(third.HasValue){layer.Set(19,third.Value);layer.Set(20,third.Value);}var compressed=ChunkDataLayer.Compress(layer,new int[15360]);
  using var target=new MemoryStream();foreach(var f in fields){if(f.Tag==1)target.Write(Field(1,compressed));else target.Write(blob,f.Start,f.End-f.Start);}File.WriteAllBytes(output,target.ToArray());Console.WriteLine("Seeded one out-of-range numeric block ID; untouched fields preserved.");
 }
 public static void SeedInventory(string input,string output,long key,bool empty=false){
  var blob=File.ReadAllBytes(input);var fields=BlockIdChunkCodec.Fields(blob);var pos=Vintagestory.Common.Database.ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)key));
  var bytes=InventoryPayload(pos.X*32+17,pos.Y*32+pos.Dimension*32768,pos.Z*32,empty);
  using var target=new MemoryStream();foreach(var f in fields)if(f.Tag!=8)target.Write(blob,f.Start,f.End-f.Start);target.Write(Field(8,bytes));File.WriteAllBytes(output,target.ToArray());
  Console.WriteLine("Seeded retained Bookshelf inventory: 7 serialized items, no block entity instantiated.");
 }
 static byte[] InventoryPayload(int x=17,int y=0,int z=0,bool empty=false){
  var tree=new TreeAttribute();tree.SetInt("posx",x);tree.SetInt("posy",y);tree.SetInt("posz",z);tree.SetString("blockCode","bookshelf");
  tree.SetString("type","2row2col");tree.SetString("material","oak");
  var inventory=new TreeAttribute();inventory.SetInt("qslots",14);var slots=new TreeAttribute();if(!empty){var stack=new ItemStack{Class=EnumItemClass.Item,Id=123,StackSize=7};stack.Attributes.SetString("text","Keep this exact written text");slots.SetItemstack("0",stack);}inventory.SetAttribute("slots",slots);tree.SetAttribute("inventory",inventory);
  tree.SetBytes("opaque",[1,2,3]);tree.SetBool("flag",true);tree.SetStringArray("strings",["a","b"]);tree["ints"]=new IntArrayAttribute([1,2]);tree.SetFloat("float",1f);tree.SetLong("long",99);tree.SetDouble("double",2);
  tree["floats"]=new FloatArrayAttribute([1,2]);tree["doubles"]=new DoubleArrayAttribute([1,2]);tree["longs"]=new LongArrayAttribute([1,2]);tree["bools"]=new BoolArrayAttribute([true,false]);tree["trees"]=new TreeArrayAttribute([new TreeAttribute()]);
  using var data=new MemoryStream();using(var writer=new BinaryWriter(data,Encoding.UTF8,true)){writer.Write("Bookshelf");tree.ToBytes(writer);}return data.ToArray();
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
  var payload=InventoryPayload();var intact=payload.ToArray();var report=SavedBlockEntityInspector.Inspect(payload);
  Assert(report.Complete&&report.Class=="Bookshelf"&&report.SavedBlockCode=="bookshelf","retained class/code read without a block entity instance");
  Assert(report.X==17&&report.Y==0&&report.Z==0&&report.Dimension==0,"saved coordinates decoded");
  Assert(report.NonEmptyStacks==1&&report.SerializedItemQuantity==7&&report.Stacks.Single().Id==123&&report.Stacks[0].InInventoryTree,"standard inventory stacks read without item resolution");
  Assert(payload.SequenceEqual(intact)&&report.PayloadSha256.Length==64,"inspection is byte-preserving and hashed");
  var builtin=TreeAttribute.AttributeIdMapping[1];try{TreeAttribute.AttributeIdMapping[1]=typeof(string);Assert(!SavedBlockEntityInspector.Inspect(payload).Complete,"overridden built-in types refused before decoding");}finally{TreeAttribute.AttributeIdMapping[1]=builtin;}
  var dimensionReport=SavedBlockEntityInspector.Inspect(InventoryPayload(y:3*32768+45));Assert(dimensionReport.Dimension==3&&dimensionReport.Y==45,"dimension-aware entity position");
  Assert(!SavedBlockEntityInspector.Inspect(payload[..^1]).Complete,"truncated tree marked partial");
  using(var unknown=new MemoryStream()){using(var w=new BinaryWriter(unknown,Encoding.UTF8,true)){w.Write("Unknown");w.Write((byte)250);w.Write("custom");w.Write(0);}Assert(!SavedBlockEntityInspector.Inspect(unknown.ToArray()).Complete,"unknown attribute refused without executing a mod attribute");}
  using(var bomb=new MemoryStream()){using(var w=new BinaryWriter(bomb,Encoding.UTF8,true)){w.Write("Unknown");w.Write((byte)8);w.Write("bytes");w.Write(int.MaxValue);}Assert(!SavedBlockEntityInspector.Inspect(bomb.ToArray()).Complete,"oversized array refused before allocation");}
  using(var deep=new MemoryStream()){using(var w=new BinaryWriter(deep,Encoding.UTF8,true)){w.Write("Unknown");for(int i=0;i<30;i++){w.Write((byte)6);w.Write("tree");}}Assert(!SavedBlockEntityInspector.Inspect(deep.ToArray()).Complete,"excessive nesting refused");}
  var cells=codec.ReadCells(blob,[15,17,23]);Assert(cells[15][0]==42&&cells[17][0]==123456&&cells[23][1]==123456,"targeted layer inspection preserves solid/fluid distinction");
  try{codec.ReadCells(blob,[-1]);throw new Exception("Missing index guard");}catch(ArgumentOutOfRangeException){count++;}
  var inventoryBlob=Blob(ChunkDataLayer.Compress(solid,scratch),ChunkDataLayer.Compress(fluid,scratch),entity:payload);
  try{codec.Replace(inventoryBlob,123456,42,out _);throw new Exception("Missing inventory protection");}catch(InvalidOperationException){Assert(true,"retained bookshelf inventory still blocks generic replacement");}
  Assert(report.InventorySlotCount==14&&report.SlotsOffset>=0&&report.ShapeType=="2row2col","standard inventory subtree and bookshelf geometry located");
  var reconnected=codec.RestoreContainer(inventoryBlob,17,0,0,0,123456,42,report.PayloadSha256,null);
  var reconnectedEntity=codec.EntityAt(reconnected,17,0,0,0);Assert(reconnectedEntity.Bytes.SequenceEqual(payload),"reconnect preserves full inventory payload and item attributes exactly");
  Assert(codec.ReadCells(reconnected,[17,32767])[17][0]==42&&codec.ReadCells(reconnected,[32767])[32767][0]==123456,"reconnect changes one cell, not every occurrence of the ID");
  try{codec.RestoreContainer(inventoryBlob,17,0,0,0,123456,42,"stale",null);throw new Exception("Missing stale data guard");}catch(InvalidOperationException){count++;}
  var desired=new TreeAttribute();desired.SetItemstack("0",new ItemStack{Class=EnumItemClass.Item,Id=99,StackSize=1});
  try{codec.RestoreContainer(inventoryBlob,17,0,0,0,123456,42,report.PayloadSha256,desired.ToBytes());throw new Exception("Missing nonempty guard");}catch(InvalidOperationException){count++;}
  var empty=InventoryPayload(empty:true);var emptySummary=SavedBlockEntityInspector.Inspect(empty);var emptyBlob=Blob(ChunkDataLayer.Compress(solid,scratch),ChunkDataLayer.Compress(fluid,scratch),entity:empty);
  var restored=codec.RestoreContainer(emptyBlob,17,0,0,0,123456,42,emptySummary.PayloadSha256,desired.ToBytes());var restoredEntity=codec.EntityAt(restored,17,0,0,0);
  Assert(restoredEntity.Summary.NonEmptyStacks==1&&restoredEntity.Summary.Stacks[0].Id==99&&restoredEntity.Summary.Stacks[0].Quantity==1,"snapshot fills the empty saved inventory");
  Assert(empty.AsSpan(0,emptySummary.SlotsOffset).SequenceEqual(restoredEntity.Bytes.AsSpan(0,emptySummary.SlotsOffset)),"block entity prefix outside inventory slots preserved byte for byte");
  Assert(empty.AsSpan(emptySummary.SlotsOffset+emptySummary.SlotsLength).SequenceEqual(restoredEntity.Bytes.AsSpan(restoredEntity.Summary.SlotsOffset+restoredEntity.Summary.SlotsLength)),"block entity suffix including metadata preserved byte for byte");
  foreach(var f in BlockIdChunkCodec.Fields(emptyBlob).Where(f=>f.Tag is not (1 or 8))){var g=BlockIdChunkCodec.Fields(restored).Single(g=>g.Tag==f.Tag);Assert(emptyBlob.AsSpan(f.Start,f.End-f.Start).SequenceEqual(restored.AsSpan(g.Start,g.End-g.Start)),"unrelated protobuf field preserved during restoration");}
  var one=codec.ReplaceAt(blob,17,0,0,0,123456,42);var oneCells=codec.ReadCells(one,[17,32767,15]);Assert(oneCells[17][0]==42&&oneCells[32767][0]==123456&&oneCells[15][0]==42,"one-position repair never replaces neighbors sharing the same ID");
  foreach(var f in BlockIdChunkCodec.Fields(blob).Where(f=>f.Tag!=1)){var g=BlockIdChunkCodec.Fields(one).Single(g=>g.Tag==f.Tag);Assert(blob.AsSpan(f.Start,f.End-f.Start).SequenceEqual(one.AsSpan(g.Start,g.End-g.Start)),"one-position repair preserves all non-solid fields");}
  try{codec.ReplaceAt(inventoryBlob,17,0,0,0,123456,42);throw new Exception("Selected repair discarded retained inventory");}catch(InvalidOperationException){count++;}
  try{codec.ReplaceAt(blob,23,0,0,0,0,42);throw new Exception("Selected repair ignored fluid");}catch(InvalidOperationException){count++;}
  try{codec.ReplaceAt(blob,17,0,0,0,99,42);throw new Exception("Selected repair ignored stale source");}catch(InvalidOperationException){count++;}
  var fromAir=codec.ReplaceAt(blob,18,0,0,0,0,42);Assert(codec.ReadCells(fromAir,[18,19])[18][0]==42&&codec.ReadCells(fromAir,[19])[19][0]==0,"explicit air restoration affects one cell only");
  var mismatch=RetainedBlockFindings.Inspect(inventoryBlob,codec,_=>"game:rock-granite",_=>"",out var unverified);Assert(mismatch.Count==1&&mismatch[0].SavedCode=="game:bookshelf"&&mismatch[0].Stacks==1&&unverified==0,"registered host IDs still expose orphaned bookshelf metadata");
  var emptyReconnect=codec.RestoreContainer(emptyBlob,17,0,0,0,123456,42,emptySummary.PayloadSha256,null);Assert(codec.EntityAt(emptyReconnect,17,0,0,0).Bytes.SequenceEqual(empty),"empty retained bookshelf metadata can be reconnected byte for byte");
  Assert(RetainedBlockFindings.MappingMismatch(42,"bookshelf","game:rock-granite")&&!RetainedBlockFindings.MappingMismatch(42,"rock-granite","game:rock-granite")&&!RetainedBlockFindings.MappingMismatch(0,"game:plant","game:air"),"mapping mismatch detection normalizes codes and never globally remaps air");
  // Mixed official-format saved layers: a genuine placeholder, healthy fluid,
  // a code-less registry hole, a valid mismatched definition, and an unknown raw ID.
  var registry=new Block?[]{new Block{BlockId=0,Code=new AssetLocation("air")},new Block{BlockId=1,Code=new AssetLocation("removed-bookshelf"),IsMissing=true},new Block{BlockId=2,Code=new AssetLocation("water-still-7")},new Block{BlockId=3,IsMissing=true},new Block{BlockId=4,Code=new AssetLocation("rock-granite")},null};
  var placeholders=registry.Select(MissingBlockScan.IsPlaceholder).ToArray();var suspects=new[]{false,true,false,true,true,true};
  var mixed=new BlockChunkDataLayer(pool);mixed.PopulateWithAir();foreach(var pair in new[]{(17,1),(18,3),(19,4),(20,999999),(21,5)})mixed.Set(pair.Item1,pair.Item2);
  var mixedFluid=new BlockChunkDataLayer(pool);mixedFluid.PopulateWithAir();mixedFluid.Set(22,2);
  byte[] mixedDecor=new byte[16];BinaryPrimitives.WriteInt32LittleEndian(mixedDecor.AsSpan(0,4),1);BinaryPrimitives.WriteInt32LittleEndian(mixedDecor.AsSpan(4,4),1);BinaryPrimitives.WriteInt32LittleEndian(mixedDecor.AsSpan(8,4),2);BinaryPrimitives.WriteInt32LittleEndian(mixedDecor.AsSpan(12,4),4);
  var mixedBytes=Blob(ChunkDataLayer.Compress(mixed,scratch),ChunkDataLayer.Compress(mixedFluid,scratch),mixedDecor);
  var onlyQuestions=codec.Inspect(mixedBytes,id=>MissingBlockScan.Includes(id,null,false,placeholders,suspects));
  Assert(onlyQuestions.Keys.Order().SequenceEqual(new[]{1,3,5,999999})&&onlyQuestions[1].SequenceEqual(new long[]{1,0,1}),"default scan includes missing definitions and undefined raw IDs while excluding healthy water and registered mismatches");
  Assert(codec.Inspect(mixedBytes,id=>MissingBlockScan.Includes(id,4,false,placeholders,suspects)).Count==0,"registered source focus cannot broaden default scan");
  Assert(codec.Inspect(mixedBytes,id=>MissingBlockScan.Includes(id,1,false,placeholders,suspects))[1][0]==1,"missing source focus retains actual placeholder");
  var diagnostic=codec.Inspect(mixedBytes,id=>MissingBlockScan.Includes(id,null,true,placeholders,suspects));
  Assert(diagnostic.Keys.Order().SequenceEqual(new[]{1,3,4,5,999999}),"advanced diagnostics retain undefined and registered-mismatch evidence");
  Assert(codec.Inspect(mixedBytes,id=>MissingBlockScan.Includes(id,4,true,placeholders,suspects)).Keys.SequenceEqual(new[]{4}),"advanced source focus is restricted to the selected registered ID");
  Assert(!MissingBlockScan.Includes(0,0,true,placeholders,suspects),"air is never a scan source");
  Assert(MissingBlockScan.Includes(-1,null,false,placeholders,suspects)&&MissingBlockScan.Includes(-1,null,true,placeholders,suspects),"invalid negative IDs are unresolved; they never identify a healthy definition");
  Assert(!MissingBlockScan.IsPlaceholder(new Block{Code=new AssetLocation("unknown")}),"unknown texture or name alone does not mark a registered block missing");
  pool.FreeArrays(mixed);pool.FreeArrays(mixedFluid);
  var batch=codec.ReplaceMany(blob,new Dictionary<int,int>{{123456,42},{42,43}},out long batchChanged);
  Assert(batchChanged==6&&codec.Inspect(batch,id=>id==42)[42].SequenceEqual(new long[]{2,1,1})&&codec.Inspect(batch,id=>id==43)[43].SequenceEqual(new long[]{1,0,1}),"simultaneous chain mapping uses original IDs once across solid, fluid and decor");
  var batchField=BlockIdChunkCodec.Fields(batch).Single(f=>f.Tag==90);Assert(blob.AsSpan(oldField.Start,oldField.End-oldField.Start).SequenceEqual(batch.AsSpan(batchField.Start,batchField.End-batchField.Start)),"batch preserves unknown protobuf fields exactly");
  var swapped=codec.ReplaceMany(blob,new Dictionary<int,int>{{123456,42},{42,123456}},out _);Assert(codec.Inspect(swapped,id=>id==123456)[123456].SequenceEqual(new long[]{1,0,1}),"cycle mapping does not cascade or erase identities");
  var removedBatch=codec.ReplaceMany(blob,new Dictionary<int,int>{{123456,0},{42,43}},out _);Assert(codec.Inspect(removedBatch,id=>id==123456).Count==0&&codec.Inspect(removedBatch,id=>id==43)[43][2]==1,"batch air removal preserves other decor mappings");
  var selectedOnly=RemapSelection.Resolve([new(123456,"game:rock-granite")],_=>42);Assert(selectedOnly.Count==1&&!selectedOnly.ContainsKey(99),"selection resolves only explicitly supplied IDs");
  foreach(var invalid in new IReadOnlyList<RemapChoice>[]{[],[new(0,"42")],[new(1,"")],[new(1,"42"),new(1,"43")],Enumerable.Range(1,33).Select(i=>new RemapChoice(i,"999")).ToList()}){try{RemapSelection.Resolve(invalid,int.Parse);throw new Exception("Invalid batch accepted");}catch(ArgumentException){count++;}}
  try{codec.ReplaceMany(inventoryBlob,new Dictionary<int,int>{{42,43},{123456,42}},out _);throw new Exception("Batch discarded inventory metadata");}catch(InvalidOperationException){count++;}
  var sampled=new List<(int Id,int Layer,int Index)>();codec.Inspect(blob,id=>id==123456,(id,layer,index)=>sampled.Add((id,layer,index)));Assert(sampled.Contains((123456,0,17))&&sampled.Contains((123456,0,32767))&&sampled.Contains((123456,1,23))&&sampled.Contains((123456,2,-1)),"samples retain exact solid/fluid cell indices and distinguish decor");
  var options=new List<BlockRemapAdvisor.Target>{new(42,"game:rock-granite",false,""),new(43,"game:rock-basalt",false,""),new(44,"other:rock-granite",false,""),new(45,"game:water-still-7",true,""),new(55,"game:bookshelf-oak",false,"Bookshelf")};
  var exact=BlockRemapAdvisor.Rank(999999,"rock-granite",1,0,0,options,[]);Assert(exact[0].Id==42&&exact[0].Confidence=="Medium"&&exact[0].Selectable,"canonical saved identity outranks weak family/cross-domain guesses");
  Assert(exact.Single(c=>c.Id==44).Confidence=="Low"&&exact.Single(c=>c.Id==43).Confidence=="Low","cross-domain and same-family suggestions remain weak");
  var placed=new BlockHistoryAdvisor.Candidate{Code="game:rock-basalt",Confidence="Medium",Reason="latest placement",Evidence=[new(1,"events","Place","game:rock-basalt",DateTime.UtcNow)]};
  var history=BlockRemapAdvisor.Rank(999999,"game:rock-andesite",1,0,0,options,[new("1,2,3 dimension 0",placed)]);Assert(history[0].Id==43&&history[0].Evidence.Any(e=>e.Contains("events #1")),"exact-location placement evidence outranks name-only variants and exposes record identity");
  var otherPlacement=new BlockHistoryAdvisor.Candidate{Code="game:rock-granite",Confidence="Medium",Reason="different sample"};
  var conflict=BlockRemapAdvisor.Rank(999999,"rock-granite",1,0,0,options,[new("sample A",placed),new("sample B",otherPlacement)]);Assert(conflict.Where(c=>c.Id is 42 or 43).All(c=>c.Confidence=="Low"&&c.Reason.Contains("no consensus")),"conflicting samples lower confidence even for exact registry identities");
  Assert(!BlockRemapAdvisor.Rank(999999,"game:water-still-7",1,0,0,options,[])[0].Selectable,"fluid target cannot replace solid source");
  Assert(!BlockRemapAdvisor.Rank(999999,"game:bookshelf-oak",1,0,0,options,[])[0].Selectable,"inventory-bearing recommendation routes to dedicated recovery");
  Assert(BlockRemapAdvisor.Rank(999999,"",1,0,0,options,[]).Count==0,"no saved identity/history produces no fabricated recommendations");
  var solidSource=new RemapTargetPolicy.Source(999999,true,1,0,0);var stone=new BlockRemapAdvisor.Target(42,"game:rock-granite",false,"");
  Assert(RemapTargetPolicy.Issue(solidSource,stone,false,false)=="","target validation accepts a compatible missing-source replacement");
  Assert(RemapTargetPolicy.Issue(solidSource,null,false,false).Contains("unregistered"),"target validation rejects unknown numeric IDs");
  Assert(RemapTargetPolicy.Issue(solidSource,new(55,"game:bookshelf",false,"Bookshelf"),false,false).Contains("inventory"),"target checks route container definitions to inventory recovery");
  Assert(RemapTargetPolicy.Issue(solidSource,new(45,"game:water",true,""),false,false).Contains("layer"),"scoped search and preview share layer restrictions");
  Assert(RemapTargetPolicy.Issue(solidSource,new(0,"game:air",false,""),false,false).Contains("Allow Air")&&RemapTargetPolicy.Issue(solidSource,new(0,"game:air",false,""),true,false)=="","air replacement needs an explicit option");
  Assert(RemapTargetPolicy.Issue(new(999999,false,1,0,0),stone,false,false).Contains("registered")&&RemapTargetPolicy.Issue(new(999999,false,1,0,0),stone,false,true).Contains("protected"),"healthy source protection cannot be bypassed by the legacy allow-valid flag");
  Assert(RemapTargetPolicy.Issue(new(42,true,1,0,0),stone,false,false).Contains("identical"),"same-ID replacement is rejected early");
  Assert(RemapTargetPolicy.Issue(new(999999,true,1,1,0),stone,false,false).Contains("layer"),"mixed-layer source cannot silently use a solid target");
  var shelfOptions=Enumerable.Range(100,8).Select(i=>new BlockRemapAdvisor.Target(i,"game:bookshelf-variant-"+i,false,"")).Append(new(55,"game:bookshelf-oak",false,"Bookshelf")).ToList();
  var restrictedExact=BlockRemapAdvisor.Rank(999999,"game:bookshelf-oak",1,0,0,shelfOptions,[]);Assert(restrictedExact[0].Id==55&&!restrictedExact[0].Selectable,"strong exact inventory identity remains visible ahead of weak usable guesses");
  var variants=BlockRemapAdvisor.Rank(999999,"game:lamp-brass-north",1,0,0,[new(100,"game:lamp-iron-west",false,""),new(101,"game:lamp-brass-east",false,"")],[]);
  Assert(variants[0].Id==101&&variants.All(c=>c.Confidence=="Low"),"shared material/variant tokens improve weak ranking without increasing confidence");
  // Exact-code reconnection must retain every container byte, and refuse
  // missing metadata, incompatible classes, positions and code mismatches.
  var autoSolid=new BlockChunkDataLayer(pool);autoSolid.PopulateWithAir();autoSolid.Set(17,123456);autoSolid.Set(15,42);
  var autoFluid=new BlockChunkDataLayer(pool);autoFluid.PopulateWithAir();
  var autoBlob=Blob(ChunkDataLayer.Compress(autoSolid,scratch),ChunkDataLayer.Compress(autoFluid,scratch),entity:payload);
  var identities=new Dictionary<int,BlockIdChunkCodec.Identity>{{123456,new("game:bookshelf","Bookshelf",false)}};
  var autoFixed=codec.ReplaceMany(autoBlob,new Dictionary<int,int>{{123456,55}},out long autoChanged,identities,new Vintagestory.Common.Database.ChunkPos(0,0,0,0));
  Assert(autoChanged==1&&codec.ReadCells(autoFixed,[17,15])[17][0]==55&&codec.ReadCells(autoFixed,[15])[15][0]==42,"exact identity migration reconnects missing host and preserves healthy neighboring ID");
  var originalEntity=BlockIdChunkCodec.Fields(autoBlob).Single(f=>f.Tag==8);var recoveredEntity=BlockIdChunkCodec.Fields(autoFixed).Single(f=>f.Tag==8);
  Assert(autoBlob.AsSpan(originalEntity.Start,originalEntity.End-originalEntity.Start).SequenceEqual(autoFixed.AsSpan(recoveredEntity.Start,recoveredEntity.End-recoveredEntity.Start)),"automatic recovery preserves whole inventory/attributes payload byte for byte");
  foreach(var identity in new[]{new BlockIdChunkCodec.Identity("game:bookshelf","Chest",false),new BlockIdChunkCodec.Identity("game:wrong","Bookshelf",false),new BlockIdChunkCodec.Identity("game:bookshelf","",false)})
  {try{codec.ReplaceMany(autoBlob,new Dictionary<int,int>{{123456,55}},out _,new Dictionary<int,BlockIdChunkCodec.Identity>{{123456,identity}});throw new Exception("Unsafe exact identity accepted");}catch(InvalidOperationException){count++;}}
  autoSolid.Set(18,123456);var missingEntity=Blob(ChunkDataLayer.Compress(autoSolid,scratch),ChunkDataLayer.Compress(autoFluid,scratch),entity:payload);
  try{codec.ReplaceMany(missingEntity,new Dictionary<int,int>{{123456,55}},out _,identities);throw new Exception("Invented missing container metadata");}catch(InvalidOperationException){count++;}
  try{codec.ReplaceMany(autoBlob,new Dictionary<int,int>{{123456,55}},out _,identities,new Vintagestory.Common.Database.ChunkPos(1,0,0,0));throw new Exception("Out-of-chunk retained metadata accepted");}catch(InvalidOperationException){count++;}
  var large=RemapSelection.Resolve(Enumerable.Range(1000,64).Select(i=>new RemapChoice(i,"42")).ToArray(),int.Parse,4096);Assert(large.Count==64,"automatic exact plan supports more than manual 32-ID limit");
  var many=codec.ReplaceMany(blob,Enumerable.Range(123456,64).ToDictionary(i=>i,_=>42),out var manyCount);Assert(manyCount==4,"chunk migration supports bounded larger exact-code plans");
  pool.FreeArrays(autoSolid);pool.FreeArrays(autoFluid);
  Assert(HeldBlockReplacementPolicy.Issue(999999,true,true,42,false,false,false,false)=="","held command accepts a missing solid and registered held block");
  Assert(HeldBlockReplacementPolicy.Issue(42,false,true,43,false,false,false,false).Contains("protected"),"held command protects healthy blocks");
  Assert(HeldBlockReplacementPolicy.Issue(0,true,true,42,false,false,false,false).Contains("protected"),"held command cannot replace air");
  Assert(HeldBlockReplacementPolicy.Issue(999999,true,false,42,false,false,false,false).Contains("active hotbar"),"held command rejects items and empty hands");
  Assert(HeldBlockReplacementPolicy.Issue(999999,true,true,42,true,false,false,false).Contains("registered"),"held command rejects missing held definitions");
  Assert(HeldBlockReplacementPolicy.Issue(999999,true,true,42,false,true,false,false).Contains("solid"),"held command protects fluid layers");
  Assert(HeldBlockReplacementPolicy.Issue(999999,true,true,42,false,false,true,false).Contains("inventory")&&HeldBlockReplacementPolicy.Issue(999999,true,true,42,false,false,false,true).Contains("inventory"),"held command protects live and uninstantiated retained inventories");
  pool.FreeAll();Console.WriteLine($"{count} block-ID codec, inventory restore and preservation checks passed against official serialization.");
 }
}
