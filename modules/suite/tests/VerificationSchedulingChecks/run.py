"""Fault/race and tick-scheduling checks using the production methods."""
from pathlib import Path
import os, subprocess, tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'src/AntiCheat/AntiCheatService.cs').read_text()
writer=s[s.index('    private readonly object verificationWriteLock'):s.index('    private void StoreModDbVerificationEntryLocked')]
writer=writer.replace('private string modDbVerificationCachePath', 'public string modDbVerificationCachePath')
refresh=s[s.index('    private readonly HashSet<string> pendingVerificationRefreshKeys'):s.index('    public (bool Success, string Message, int UniqueTargets')]
harness=Path(__file__).with_name('harness.cs.txt').read_text().replace('/*WRITER*/',writer).replace('/*REFRESH*/',refresh)
with tempfile.TemporaryDirectory() as folder:
 p=Path(folder); (p/'Program.cs').write_text(harness)
 lib=Path(os.environ['VINTAGE_STORY'])/'Lib/Newtonsoft.Json.dll'
 (p/'Test.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>{lib}</HintPath></Reference></ItemGroup></Project>')
 subprocess.run([str(Path(os.environ['DOTNET_ROOT'])/'dotnet'),'run','--project',str(p/'Test.csproj'),'-c','Release'],check=True)
