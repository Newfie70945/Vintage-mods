from pathlib import Path
import subprocess,sys
subprocess.run([sys.executable,str(Path(__file__).resolve().parents[2]/"tools/build.py"),"--module","website-connector",*sys.argv[1:]],check=True)
