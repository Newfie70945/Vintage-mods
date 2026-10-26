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
var exporter=asm.GetType("BlockLogger.WebMap.WebMapTerrainExporter",true)!;
const BindingFlags S=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
void Check(bool c,string d){if(!c)throw new Exception(d);}

// Hillshade: on a uniform slope the tile edge must shade like the interior (no seam).
int size=16;
var tile=new byte[size*size*4];
for(int z=0;z<size;z++)for(int x=0;x<size;x++){int i=(z*size+x)*4;tile[i]=100;tile[i+1]=120;tile[i+2]=80;tile[i+3]=(byte)(40+x*3);}
exporter.GetMethod("ApplyHillshade",S)!.Invoke(null,[tile,size]);
int row=8;int At(int x)=>tile[(row*size+x)*4];
Check(At(0)==At(8)&&At(size-1)==At(8),"edge pixels shade like the interior on a uniform slope: "+At(0)+","+At(8)+","+At(size-1));
Check(At(8)!=100,"hillshade changes the colour");
var flat=new byte[size*size*4];
for(int i=0;i<size*size;i++){flat[i*4]=100;flat[i*4+1]=120;flat[i*4+2]=80;flat[i*4+3]=60;}
exporter.GetMethod("ApplyHillshade",S)!.Invoke(null,[flat,size]);
Check(Enumerable.Range(0,size*size).All(p=>flat[p*4]==flat[0]),"flat terrain shades uniformly, edges included");
var scale=exporter.GetMethod("ScaleSpan",S)!;
Check((int)scale.Invoke(null,[5,2,2])!==5,"full span unchanged");
Check((int)scale.Invoke(null,[3,2,1])!==6,"half span is scaled up");
Check((int)scale.Invoke(null,[3,2,0])!==0,"zero span is safe");

// PNG round trip through the pooled decoder used by pyramid building.
var writer=exporter.GetNestedType("PngWriter",BindingFlags.NonPublic)!;
var reader=exporter.GetNestedType("PngReader",BindingFlags.NonPublic)!;
string dir=Path.Combine(Path.GetTempPath(),"nss-png-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
try{
 int n=32;var pixels=new byte[n*n*4];var rng=new Random(3);rng.NextBytes(pixels);
 string file=Path.Combine(dir,"t.png");
 writer.GetMethod("WriteRgba",S)!.Invoke(null,[file,n,n,pixels]);
 var dest=new byte[n*n*4+100];
 object? ok=reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[file,dest,n]);
 Check((bool)ok!,"pooled decoder reads our PNG");
 Check(dest.AsSpan(0,n*n*4).SequenceEqual(pixels),"decoded pixels match what was written");
 Check(!(bool)reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[file,dest,n*2])!,"wrong expected size is rejected");
 Check(!(bool)reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[file,new byte[10],n])!,"too-small destination is rejected");
 File.WriteAllBytes(Path.Combine(dir,"bad.png"),new byte[200]);
 Check(!(bool)reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[Path.Combine(dir,"bad.png"),dest,n])!,"garbage file is rejected");
 // A missing IEND/CRC must not turn a partial cache file into a successful decode.
 var complete=File.ReadAllBytes(file);
 File.WriteAllBytes(Path.Combine(dir,"truncated.png"),complete[..^5]);
 Check(!(bool)reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[Path.Combine(dir,"truncated.png"),dest,n])!,"truncated chunk is rejected");
 Check(!(bool)reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[file,dest,int.MaxValue])!,"oversized dimensions rejected before allocation");
 // Warm pools, then compare actual allocating and caller-buffer decoders on the same tile.
 int large=256;var noise=new byte[large*large*4];rng.NextBytes(noise);
 string largeFile=Path.Combine(dir,"large.png");writer.GetMethod("WriteRgba",S)!.Invoke(null,[largeFile,large,large,noise]);
 var reusable=new byte[noise.Length];var pooled=reader.GetMethod("TryReadRgbaInto",S)!;var allocating=reader.GetMethod("TryReadRgba",S)!;
 pooled.Invoke(null,[largeFile,reusable,large]);allocating.Invoke(null,new object?[]{largeFile,0,0,null});
 long Measure(bool reuse){long start=GC.GetAllocatedBytesForCurrentThread();for(int k=0;k<8;k++){
  object?[] args=reuse?new object?[]{largeFile,reusable,large}:new object?[]{largeFile,0,0,null};
  Check((bool)(reuse?pooled:allocating).Invoke(null,args)!,"measured decode succeeds");
 }return GC.GetAllocatedBytesForCurrentThread()-start;}
 long oldBytes=Measure(false),newBytes=Measure(true);
 Check(newBytes<oldBytes/2,"pooled decoder reduces managed allocations: "+oldBytes+" -> "+newBytes);
 Console.WriteLine($"PNG decode allocations (8 x 256px, warm): stock={oldBytes}, pooled={newBytes}");
 // Repeated writes and pooled reads must not leak pixels from the first image.
 var second=new byte[n*n*4];writer.GetMethod("WriteRgba",S)!.Invoke(null,[file,n,n,second]);
 Check((bool)reader.GetMethod("TryReadRgbaInto",S)!.Invoke(null,[file,dest,n])!&&dest.AsSpan(0,n*n*4).ToArray().All(x=>x==0),"reused writer scratch does not leak the previous image");
}finally{Directory.Delete(dir,true);}
Console.WriteLine("PASS: hillshade edges and pooled PNG round trip");
