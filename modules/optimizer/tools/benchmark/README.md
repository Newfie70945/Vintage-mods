# Benchmark tools

See [`../../docs/benchmark-methodology.md`](../../docs/benchmark-methodology.md) for the full methodology, prepared-clone requirements, schema, commands, limitations and dashboard guide.

The included `example.json` is an unconfigured local template, **not a working game server**. It deliberately references files you must prepare. The C intervention placeholder must be replaced with an actual independently enabled change. No optimization has earned a production performance claim from the included fixtures.

Run tests from the repository root:

```sh
python3 -m unittest discover -s tools/benchmark/tests -v
```

Synthetic preview (never measured results):

```sh
python3 tools/benchmark/fixtures/generate_synthetic.py --output /tmp/nos-synthetic
```
