using BlockLogger.AntiCheat;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var index = new OreShieldExposedIndex();
Span<int> window = stackalloc int[128];
Check(index.CopyWindow(0, window, out int next) == 0 && next == OreShieldExposedIndex.Volume, "Empty chunks complete without allocating a full cell array.");
Check(!index.Add(-1) && !index.Add(OreShieldExposedIndex.Volume), "Reject out-of-chunk entries.");
for (int i = 0; i < OreShieldExposedIndex.Volume; i++) Check(index.Add(i), "Populate dense exposed chunk.");
Check(index.Count == OreShieldExposedIndex.Volume && !index.Add(64), "Duplicate entries do not inflate density.");
int start = 0, expected = 0, windows = 0;
do
{
    int count = index.CopyWindow(start, window, out next);
    Check(count <= 128, "Dense chunks cannot exceed the requested candidate window.");
    for (int i = 0; i < count; i++) Check(window[i] == expected++, "Every dense entry is visited exactly once in order.");
    start = next;
    windows++;
} while (next < OreShieldExposedIndex.Volume);
Check(expected == OreShieldExposedIndex.Volume && windows == 256, "Dense chunk is split across bounded windows.");

// Edits between ticks must not invalidate a stored enumerator or jump over an upcoming entry.
Check(index.Remove(128) && !index.Remove(128) && index.Add(128), "Mutation is idempotent.");
Check(index.Remove(129), "Remove upcoming cell.");
int read = index.CopyWindow(128, window[..3], out next);
Check(read == 3 && window[0] == 128 && window[1] == 130 && window[2] == 131 && next == 132, "Resume after additions/removals between ticks.");
Check(index.CopyWindow(OreShieldExposedIndex.Volume, window, out next) == 0 && next == OreShieldExposedIndex.Volume, "Completed cursors remain complete.");
Check(index.CopyWindow(132, Span<int>.Empty, out next) == 0 && next == 132, "Empty destination does not consume entries.");

var sparse = new OreShieldExposedIndex();
foreach (int cell in new[] { 0, 1, 63, 64, 65, 1023, 32767 }) sparse.Add(cell);
read = sparse.CopyWindow(64, window, out next);
Check(read == 4 && window[0] == 64 && window[1] == 65 && window[2] == 1023 && window[3] == 32767 && next == 32768, "Sparse reads cross word boundaries without revisiting earlier entries.");
index.CopyWindow(0, window, out _);
long beforeCopies = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 10000; i++) index.CopyWindow(i % 32768, window, out _);
Check(GC.GetAllocatedBytesForCurrentThread() == beforeCopies, "Repeated production index windows allocate no arrays or enumerators.");

Span<int> faceCells = stackalloc int[1024];
var denseCells = new HashSet<int>(Enumerable.Range(0, 32768));
foreach (bool fixedX in new[] { false, true })
foreach (int coordinate in new[] { 0, 31 })
{
    int count = OreShieldBoundaryWindow.CopyHorizontalFace(denseCells, fixedX, coordinate, faceCells);
    Check(count == 1024, "Dense geology reads at most one face, rather than the entire chunk.");
    var seen = new HashSet<int>();
    for (int i = 0; i < count; i++)
    {
        int axis = fixedX ? faceCells[i] & 31 : (faceCells[i] >> 5) & 31;
        Check(axis == coordinate && seen.Add(faceCells[i]), "Boundary indices touch the requested face exactly once.");
    }
    var sparseCells = new HashSet<int>(new[] { -1, 0, 31, 32, 1023, 32767, 32768 });
    count = OreShieldBoundaryWindow.CopyHorizontalFace(sparseCells, fixedX, coordinate, faceCells);
    int expectedCount = sparseCells.Count(cell => cell >= 0 && cell < 32768 && (fixedX ? cell & 31 : (cell >> 5) & 31) == coordinate);
    Check(count == expectedCount, "Sparse boundary filtering matches dense face probing and excludes invalid entries.");
}

for (int radius = 1; radius <= 6; radius++)
{
    var cursor = new OreShieldScanCursor();
    cursor.Configure(-2, 1024, 8, radius, 1);
    var seen = new HashSet<OreShieldScanCursor.Offset>();
    int lastD2 = -1;
    do
    {
        var offset = cursor.Current;
        int d2 = offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z;
        Check(d2 >= lastD2 && seen.Add(offset), "Sweep visits nearby chunks first without duplicate coordinates.");
        Check(Math.Abs(offset.X) <= radius && Math.Abs(offset.Y) <= radius && Math.Abs(offset.Z) <= radius, "Offsets stay in requested radius.");
        lastD2 = d2;
        cursor.LocalIndex = 157;
    } while (cursor.AdvanceChunk());
    Check(seen.Count == (int)Math.Pow(radius * 2 + 1, 3) && !cursor.HasPendingSweep && cursor.LocalIndex == 0, "Entire radius completes once, including dimension-local vertical offsets.");
    cursor.BeginSweep();
    cursor.LocalIndex = 345;
    var current = cursor.Current;
    cursor.Configure(-2, 1024, 8, radius, 1);
    Check(cursor.LocalIndex == 345 && cursor.Current == current, "Ordinary motion/view updates retain sweep progress.");
    cursor.Configure(-1, 1024, 8, radius, 1);
    Check(cursor.LocalIndex == 0 && cursor.Current == default && cursor.HasPendingSweep, "Crossing chunk boundaries starts at the new nearest chunk.");
    cursor.LocalIndex = 45;
    cursor.Configure(-1, 1024, 8, radius, 2);
    Check(cursor.LocalIndex == 0, "Dimension changes reset cursors.");
}

foreach (int limit in new[] { 0, 1, 2, 64, 2048 })
{
    var shared = new OreShieldWorkBudget(limit);
    int performed = 0;
    // Deliberately more players than the previous minimum-eight fairness allocation supported.
    for (int player = 0; player < 1024; player++)
    for (int ray = 0; ray < 8; ray++)
        if (shared.TryTake()) performed++;
    Check(performed == limit && shared.Used == limit && shared.Remaining == 0, "Shared work ceiling holds across a crowded server.");
}
Console.WriteLine("PASS: production OreShield dense/sparse index windows, allocation-free continuation, face-bounded refresh, nearest-first sweep coverage/reset and shared work ceilings.");

var cells=new OreShieldDiscoveryCells();var reference=new HashSet<int>();var random=new Random(12345);
Check(!cells.Add(-1)&&!cells.Add(32768),"Invalid discovery cells rejected");
for(int i=0;i<32768;i++){Check(cells.Add(i)==reference.Add(i),"Dense discovery insertion differs");}
Check(cells.IsDense&&cells.Count==32768&&cells.ToHashSet().SetEquals(reference),"Promotion preserves every discovery");
for(int i=0;i<100000;i++){int cell=random.Next(32768);bool add=random.Next(2)==0;Check((add?cells.Add(cell):cells.Remove(cell))==(add?reference.Add(cell):reference.Remove(cell)),"Discovery edits differ from HashSet");Check(cells.Count==reference.Count&&cells.Contains(cell)==reference.Contains(cell),"Discovery count/lookup drift");}
Check(cells.ToHashSet().SetEquals(reference),"Dense enumeration differs after edits");
Console.WriteLine("PASS: compact discovery sparse/dense promotion, all 32,768 cells, invalid/duplicate entries and 100,000 randomized edits match HashSet semantics.");

var isolated=cells.Clone();int originalCount=cells.Count;isolated.Add(0);isolated.Remove(32767);
Check(cells.Count==originalCount&&cells.ToHashSet().SetEquals(reference),"Recipient snapshot edits changed shared discoveries");

if(args.Contains("--benchmark"))
{
    const int chunks=64;
    (long Bytes,long Milliseconds) Measure(bool compact)
    {
        GC.Collect();var watch=System.Diagnostics.Stopwatch.StartNew();long before=GC.GetAllocatedBytesForCurrentThread();
        for(int chunk=0;chunk<chunks;chunk++)
        {
            if(compact){var record=new OreShieldDiscoveryCells();for(int i=0;i<32768;i++)record.Add(i);GC.KeepAlive(record);}
            else {var record=new HashSet<int>();for(int i=0;i<32768;i++)record.Add(i);GC.KeepAlive(record);}
        }
        return (GC.GetAllocatedBytesForCurrentThread()-before,watch.ElapsedMilliseconds);
    }
    var baseline=Measure(false);var compact=Measure(true);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {Fixture="64 dense discovery records, 32768 cells each",HashSetAllocatedBytes=baseline.Bytes,CompactAllocatedBytes=compact.Bytes,HashSetMilliseconds=baseline.Milliseconds,CompactMilliseconds=compact.Milliseconds}));
}
