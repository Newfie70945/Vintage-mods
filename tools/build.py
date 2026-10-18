#!/usr/bin/env python3
"""Build and package restored mods against an owner-provided official game install."""
from pathlib import Path
import argparse,json,os,subprocess,zipfile,sys
root=Path(__file__).resolve().parents[1]
projects=json.loads((root/'modules/manifest.json').read_text())['mods']
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--module',action='append',choices=[x['id'] for x in projects]);parser.add_argument('--with-optimizer',action='store_true',help='Build NOS with its checks and package it with the Suite');args=parser.parse_args()
if args.with_optimizer and args.module and 'suite' not in args.module:raise SystemExit('--with-optimizer requires the suite module')
game=Path(os.environ.get('VINTAGE_STORY','')).resolve()
if not (game/'VintagestoryAPI.dll').is_file():raise SystemExit('Set VINTAGE_STORY to your official Vintage Story 1.22.7 installation.')
provided={p.name.lower() for p in game.glob('*.dll')}|{p.name.lower() for p in (game/'Lib').glob('*.dll')}|{p.name.lower() for p in (game/'Mods').glob('*.dll')}
out=root/'artifacts';out.mkdir(exist_ok=True)
for item in projects:
 if args.module and item['id'] not in args.module:continue
 source=root/item['path'];assembly=item['assembly'];build=out/'build'/item['id'];build.mkdir(parents=True,exist_ok=True)
 subprocess.run(['dotnet','build',str(source/(assembly+'.csproj')),'-c','Release','--nologo',f'-p:OutputPath={build}/','-p:AppendTargetFrameworkToOutputPath=false'],check=True)
 version=json.loads((source/'modinfo.json').read_text())['version'];target=out/(assembly+'V'+version+'.zip')
 with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED) as archive:
  own=assembly+'.dll';archive.write(build/own,own)
  archive.write(source/'modinfo.json','modinfo.json')
  # Companions such as Discord Link need their NuGet-managed runtime dependencies.
  for path in sorted(build.glob('*.dll')):
   if path.name==own or path.name.lower() in provided:continue
   archive.write(path,path.name)
  for folder in ['assets','guides']:
   for path in sorted((source/folder).rglob('*')):
    if path.is_file() and path.suffix.lower() not in ['.dll','.pdb','.zip']:archive.write(path,path.relative_to(source))
  if (source/'modicon.png').is_file():archive.write(source/'modicon.png','modicon.png')
  for path in sorted(source.glob('*.example.json')):archive.write(path,path.name)
 print(target)

if args.with_optimizer:
 subprocess.run([sys.executable,str(root/'modules/optimizer/tools/build.py')],check=True)
 suite_version=next(x['version'] for x in projects if x['id']=='suite')
 optimizer_version=(root/'modules/optimizer/VERSION').read_text().strip()
 pair=out/('Newfies-Suite-'+suite_version+'-Optimizer-'+optimizer_version+'.zip')
 with zipfile.ZipFile(pair,'w',zipfile.ZIP_DEFLATED) as archive:
  for path in [out/('NewfiesServerEssentialsSuiteV'+suite_version+'.zip'),root/'modules/optimizer/artifacts'/('NewfiesServerOptimizer-'+optimizer_version+'.zip')]:archive.write(path,path.name)
  archive.writestr('INSTALL.txt','Extract this download. Install BOTH inner mod ZIPs in the game server Mods folder. Requires official Vintage Story 1.22.7. Open Suite > Optimizations > Newfies Server Optimizer. Do not install this outer ZIP as a mod. Back up your world/configuration before updating. Other companion mods are unchanged.\n')
 print(pair)
