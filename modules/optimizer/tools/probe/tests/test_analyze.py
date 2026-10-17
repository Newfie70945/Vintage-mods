import gzip,importlib.util,json,tempfile,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('probe_analyzer',Path(__file__).resolve().parents[1]/'analyze.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
class CaptureTests(unittest.TestCase):
 def fixture(self,body,compressed=False):
  folder=tempfile.TemporaryDirectory();self.addCleanup(folder.cleanup);p=Path(folder.name)/'capture';data=json.dumps(body).encode();p.write_bytes(gzip.compress(data) if compressed else data);return p
 def test_plain_and_gzip(self):
  data={'schema':2,'kind':'lithos-probe','profile':{'threads':[{'name':'ServerMain.Process','children':[{'full':'StratumServer.Sample','selfManaged':12}]}]},'series':{'tiers':[{'spanSeconds':1,'values':{'tps':[10,20,30]}}]}}
  for compressed in [False,True]:
   report=module.analyze(self.fixture(data,compressed));self.assertTrue(report['stratumRuntimeInStacks']);self.assertEqual(report['topMainThreadSelfManagedSamples'][0]['samples'],12);self.assertEqual(report['seriesSummary']['tps']['median'],20)
 def test_wrong_schema(self):
  with self.assertRaises(ValueError):module.analyze(self.fixture({'schema':1,'kind':'other'}))
 def test_invalid_json(self):
  p=self.fixture({});p.write_bytes(b'not json')
  with self.assertRaises(ValueError):module.analyze(p)
 def test_decompressed_cap(self):
  p=self.fixture({});p.write_bytes(gzip.compress(b'0'*(1024*1024)));original=module.MAX;module.MAX=65536
  try:
   with self.assertRaises(ValueError):module.analyze(p)
  finally:module.MAX=original
if __name__=='__main__':unittest.main()
