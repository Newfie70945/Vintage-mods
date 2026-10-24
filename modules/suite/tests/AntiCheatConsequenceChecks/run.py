"""Exercise the current production consequence method with stubbed actions, not game/network fixtures."""
from pathlib import Path
import os, subprocess, tempfile
root=Path(__file__).resolve().parents[2]
source=(root/'src/AntiCheat/AntiCheatService.cs').read_text()
a=source.index('    private bool ApplyConsequences(')
b=source.index('    private void EnforceClientVersionMismatchDisconnect',a)
method=source[a:b].replace('private bool ApplyConsequences','public bool ApplyConsequences',1)
with tempfile.TemporaryDirectory() as folder:
 p=Path(folder)
 (p/'Program.cs').write_text(Path(__file__).with_name('harness.cs.txt').read_text()+method+'\n}')
 (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
 subprocess.run([str(Path(os.environ['DOTNET_ROOT'])/'dotnet'),'run','--project',str(p/'Test.csproj'),'-c','Release'],check=True)
