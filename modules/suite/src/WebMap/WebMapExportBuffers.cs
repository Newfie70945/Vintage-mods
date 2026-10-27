using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace BlockLogger.WebMap;

internal sealed partial class WebMapTerrainExporter
{
    // Export worker contexts inherit this scope; normal server ArrayPool caches
    // are neither populated with our large tile buffers nor trimmed globally.
    private static readonly AsyncLocal<ExportBytePool?> exportBytePool = new();
    private static ArrayPool<byte> ExportByteBuffers => exportBytePool.Value ?? (ArrayPool<byte>)ArrayPool<byte>.Shared;
    private static ExportBufferScope BeginExportBuffers() => new();
    private sealed class ExportBufferScope : IDisposable
    {
        private readonly ExportBytePool? previous = exportBytePool.Value;
        internal readonly ExportBytePool Pool = new();
        private bool disposed;
        public ExportBufferScope() => exportBytePool.Value = Pool;
        public void Dispose(){if(disposed)return;disposed=true;Pool.Dispose();exportBytePool.Value=previous;}
    }
    private sealed class ExportBytePool : ArrayPool<byte>, IDisposable
    {
        private const int MaxArray = 4 * 1024 * 1024;
        private const long CacheLimit = 16 * 1024 * 1024;
        private readonly object gate = new();
        private readonly Dictionary<int,Stack<byte[]>> buckets = new();
        private bool disposed;
        private long retained, peak;
        internal long RetainedBytes { get { lock(gate)return retained; } }
        internal long PeakRetainedBytes { get { lock(gate)return peak; } }
        public override byte[] Rent(int minimumLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
            if(minimumLength==0)return Array.Empty<byte>();
            int size=minimumLength>MaxArray?minimumLength:256;
            while(size<minimumLength)size*=2;
            lock(gate)
                if(!disposed&&buckets.TryGetValue(size,out var bucket)&&bucket.Count>0){retained-=size;return bucket.Pop();}
            return new byte[size];
        }
        public override void Return(byte[] array,bool clearArray=false)
        {
            ArgumentNullException.ThrowIfNull(array);
            if(clearArray)Array.Clear(array);
            if(array.Length==0||array.Length>MaxArray)return;
            lock(gate)
            {
                if(disposed||retained+array.LongLength>CacheLimit)return;
                if(!buckets.TryGetValue(array.Length,out var bucket))buckets[array.Length]=bucket=new();
                bucket.Push(array);retained+=array.Length;peak=Math.Max(peak,retained);
            }
        }
        public void Dispose(){lock(gate){disposed=true;buckets.Clear();retained=0;}}
    }
    private void ReleaseExportResources(WebMapTerrainExportStatus status,ExportBufferScope buffers)
    {
        status.ExportBufferCacheBytesReleased=buffers.Pool.RetainedBytes;
        status.ExportBufferCachePeakBytes=buffers.Pool.PeakRetainedBytes;
        buffers.Dispose();
        blockColorCache.Clear();blockColorCache.TrimExcess();blockColorById=Array.Empty<Rgb>();Volatile.Write(ref blockPalettePrimed,0);
        lock(sampledBlockCountsLock){sampledBlockCounts.Clear();sampledBlockCounts.TrimExcess();}
        capturedResidentChunks.Clear();recentTileMilliseconds.Clear();
        status.ExportResourcesReleased=true;
        status.ManagedHeapBytesAtEnd=GC.GetTotalMemory(false);
        status.ProcessWorkingSetBytesAtEnd=ReadProcessWorkingSet();
    }
}
