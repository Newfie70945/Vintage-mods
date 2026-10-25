"""Compare production player persistence with its preceding implementation on SQLite."""
from pathlib import Path
import os,subprocess,tempfile
repo=Path(__file__).resolve().parents[4]
s=(repo/'modules/suite/src/Admin/AdminStorageService.cs').read_text()
old=subprocess.check_output(['git','show','8a0c65b:modules/suite/src/Admin/AdminStorageService.cs'],cwd=repo,text=True)
def method(text,name):
 a=text.index('    private static void UpsertPlayers(');b=text.index('    private static long UpsertWarnings',a)
 return text[a:b].replace('private static void UpsertPlayers','public static void '+name,1)
schema=s[s.index('CREATE TABLE IF NOT EXISTS players ('):];schema=schema[:schema.index(');')+2]
code=Path(__file__).with_name('harness.cs.txt').read_text().replace('/*METHODS*/',method(old,'Previous')+method(s,'Current')).replace('/*SCHEMA*/',schema)
with tempfile.TemporaryDirectory() as folder:
 p=Path(folder);(p/'Program.cs').write_text(code)
 game=Path(os.environ['VINTAGE_STORY'])
 refs=''.join(f'<Reference Include="{name}"><HintPath>{game}/Lib/{name}.dll</HintPath></Reference>' for name in ['Microsoft.Data.Sqlite','SQLitePCLRaw.core','SQLitePCLRaw.provider.e_sqlite3'])
 (p/'Test.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>{refs}</ItemGroup></Project>')
 env=os.environ.copy();env['LD_LIBRARY_PATH']=str(game/'Lib')
 subprocess.run([str(Path(env['DOTNET_ROOT'])/'dotnet'),'run','--project',str(p/'Test.csproj'),'-c','Release'],env=env,check=True)
