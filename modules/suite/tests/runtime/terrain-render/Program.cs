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
var palette=exporter.GetNestedType("TerrainPalette",BindingFlags.NonPublic)!;
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
Check((int)scale.Invoke(null,[5,2,2])==5,"full span unchanged");
Check((int)scale.Invoke(null,[3,2,1])==6,"half span is scaled up");
Check((int)scale.Invoke(null,[3,2,0])==0,"zero span is safe");

// Water depth tint: shallow is paler than deep; land and unknown depth are untouched.
var tint=exporter.GetMethod("ApplyWaterDepthTint",S)!;
int cells=16;var buffer=new byte[cells*4+cells];
for(int p=0;p<cells;p++){buffer[p*4]=54;buffer[p*4+1]=104;buffer[p*4+2]=145;buffer[p*4+3]=200;}
buffer[cells*4+0]=1;buffer[cells*4+1]=40;buffer[cells*4+2]=0;buffer[cells*4+3]=9;
buffer[3*4]=120;buffer[3*4+1]=100;buffer[3*4+2]=60;
tint.Invoke(null,[buffer,4]);
Check(buffer[1]>buffer[5],"shallow water has a brighter green channel than deep: "+buffer[1]+" vs "+buffer[5]);
Check(buffer[2*4]==54&&buffer[2*4+2]==145,"unknown depth keeps the palette colour");
Check(buffer[3*4]==120&&buffer[3*4+2]==60,"land is never tinted");

// Colour override parsing.
var parse=palette.GetMethod("TryParseOverride",S)!;
object?[] P(string pattern,string hex)=>new object?[]{pattern,hex,null};
var good=P("game:rock-limestone","#ff00aa");
Check((bool)parse.Invoke(null,good)!,"valid override parses");
var rec=good[2]!;
Check((string?)rec.GetType().GetProperty("Pattern")!.GetValue(rec)=="game:rock-limestone","pattern kept");
Check(!(bool)parse.Invoke(null,P("*","#ffffff"))!,"bare wildcard rejected");
Check(!(bool)parse.Invoke(null,P("a*b","#ffffff"))!,"inner wildcard rejected");
Check(!(bool)parse.Invoke(null,P("*a*","#ffffff"))!,"double wildcard rejected");
Check(!(bool)parse.Invoke(null,P("rock-*","ff00aa"))!&&!(bool)parse.Invoke(null,P("rock-*","#ff00"))!&&!(bool)parse.Invoke(null,P("rock-*","#gg0000"))!,"bad colours rejected");
Check(!(bool)parse.Invoke(null,P("has space","#ffffff"))!&&!(bool)parse.Invoke(null,P("","#ffffff"))!,"bad patterns rejected");
Check((bool)parse.Invoke(null,P("rock-*","#FFFFFF"))!&&(bool)parse.Invoke(null,P("*-limestone","#000000"))!,"prefix and suffix wildcards accepted");
Console.WriteLine("PASS: hillshade edges, water depth tint, colour override parsing");
