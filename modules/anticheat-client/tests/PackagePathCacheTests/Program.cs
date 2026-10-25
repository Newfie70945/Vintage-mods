using System.IO.Compression;
using System.Security.Cryptography;
using NewfiesAntiCheatClient;
string root=Path.Combine(Path.GetTempPath(),"nss-path-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
 string upper=Path.Combine(root,"Pack.bin"),lower=Path.Combine(root,"pack.bin");
 File.WriteAllBytes(upper,[1,2,3]);File.WriteAllBytes(lower,[4,5,6]);
 void Check(bool pass,string why){if(!pass)throw new Exception(why);}
 string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
 if(!File.ReadAllBytes(upper).SequenceEqual(File.ReadAllBytes(lower)))
 {
 DateTime stamp=new(2026,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(upper,stamp);File.SetLastWriteTimeUtc(lower,stamp);
 Check(ModContentHasher.HashFile(upper)==Hash(upper),"first file hash");
 Check(ModContentHasher.HashFile(lower)==Hash(lower),"distinct case-sensitive file must not reuse another path's hash");
 Check(ModContentHasher.FingerprintPackage(upper).PackageSha256==Hash(upper),"first package cache");
 Check(ModContentHasher.FingerprintPackage(lower).PackageSha256==Hash(lower),"distinct package path must not reuse another package fingerprint");
 string zipA=Path.Combine(root,"Pack.zip"),zipB=Path.Combine(root,"pack.zip");
 foreach(var item in new[]{(zipA,(byte)1),(zipB,(byte)2)})
 {using var zip=ZipFile.Open(item.Item1,ZipArchiveMode.Create);var entry=zip.CreateEntry("mod.bin",CompressionLevel.NoCompression);entry.LastWriteTime=new DateTimeOffset(stamp);using var output=entry.Open();output.Write([item.Item2,item.Item2,item.Item2]);}
 File.SetLastWriteTimeUtc(zipA,stamp);File.SetLastWriteTimeUtc(zipB,stamp);
 Check(new FileInfo(zipA).Length==new FileInfo(zipB).Length,"equal-size/timestamp fixture");
 Check(ModContentHasher.FingerprintPackage(zipA).PackageSha256==Hash(zipA),"first ZIP fingerprint");
 Check(ModContentHasher.FingerprintPackage(zipB).PackageSha256==Hash(zipB),"second ZIP fingerprint remains independent");
 Check(ModContentHasher.HashNormalizedContent(zipA)!=ModContentHasher.HashNormalizedContent(zipB),"ZIP content hashes remain independent");
 Console.WriteLine("PASS 8 case-sensitive file/package/ZIP cache checks with equal sizes and timestamps");
 } else Console.WriteLine("SKIP case-only path checks: filesystem aliases those names.");
 string locked=Path.Combine(root,"locked.bin");File.WriteAllBytes(locked,[7,8,9]);
 using(var handle=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
  Check(ModContentHasher.FingerprintPackage(locked).PackageSha256.Length==0,"locked file unavailable");
 Check(ModContentHasher.FingerprintPackage(locked).PackageSha256==Hash(locked),"transient package failure is retried without metadata changes");
 string manifestZip=Path.Combine(root,"manifest.zip");
 using(var zip=ZipFile.Open(manifestZip,ZipArchiveMode.Create))
 {
  using(var text=new StreamWriter(zip.CreateEntry("client-integrity.manifest.json").Open()))text.Write("{\"buildId\":\"retry-build\"}");
  using(var signature=zip.CreateEntry("client-integrity.manifest.sig").Open())signature.Write([1,2,3]);
 }
 using(var handle=new FileStream(manifestZip,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
  Check(SignedBuildManifestClient.Read(manifestZip).BuildId.Length==0,"locked manifest unavailable");
 Check(SignedBuildManifestClient.Read(manifestZip).BuildId=="retry-build","transient manifest failure is retried without metadata changes");
 var bounded=typeof(SignedBuildManifestClient).GetMethod("ReadBounded",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
 using(var stream=new MemoryStream(new byte[100_000]))
 {
  try {bounded.Invoke(null,[stream,4L]);throw new Exception("Accepted expanded entry beyond declared length");}
  catch(System.Reflection.TargetInvocationException ex)when(ex.InnerException is InvalidDataException){}
  Check(stream.Position==5,"bounded manifest read consumes at most declared length plus one");
 }
 using(var shortStream=new MemoryStream(new byte[2]))
 {
  try {bounded.Invoke(null,[shortStream,4L]);throw new Exception("Accepted truncated entry");}
  catch(System.Reflection.TargetInvocationException ex)when(ex.InnerException is InvalidDataException){}
 }
 Console.WriteLine("PASS transient file/manifest retry and declared-entry/truncation bounds");
}
finally{Directory.Delete(root,true);}
