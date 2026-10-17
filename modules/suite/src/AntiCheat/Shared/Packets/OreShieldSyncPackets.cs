using System;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared
{
    public static class OreShieldSyncProtocol
    {
        public const string ChannelName = "newfiesoreshieldsync";
        public const int Version = 3;
        public const int MaxBatchEntries = 128;
        // Independent of the public Anti-Cheat client version. This marker lets OreShield
        // reject stale experimental Client Integrity DLLs that predate the current sync/remesh contract.
        public const string Capability = "sync-remesh-v3";
        public const string BuildMarker = "ac1127-oreshield-v329-perf1";
    }
}

namespace NewfiesAntiCheat.Shared.Packets
{
    [ProtoContract]
    public sealed class OreShieldSyncBatchPacket
    {
        [ProtoMember(1)] public int ProtocolVersion { get; set; } = NewfiesAntiCheat.Shared.OreShieldSyncProtocol.Version;
        [ProtoMember(2)] public long Sequence { get; set; }
        [ProtoMember(3)] public int ChunkX { get; set; }
        [ProtoMember(4)] public int ChunkY { get; set; }
        [ProtoMember(5)] public int ChunkZ { get; set; }
        [ProtoMember(6)] public long Revision { get; set; }
        [ProtoMember(7)] public int[] LocalIndices { get; set; } = Array.Empty<int>();
        [ProtoMember(8)] public int[] BlockIds { get; set; } = Array.Empty<int>();
        // 0 = stabilize/remesh the virtual ore view; 1 = apply the authoritative world state once.
        // Apply-once packets supersede stale reveal cells after mining/placement without pinning
        // air or a replacement block for the normal 2.5 second reveal stabilization window.
        [ProtoMember(9)] public int ApplyMode { get; set; }
    }

    [ProtoContract]
    public sealed class OreShieldSyncAckPacket
    {
        [ProtoMember(1)] public int ProtocolVersion { get; set; } = NewfiesAntiCheat.Shared.OreShieldSyncProtocol.Version;
        [ProtoMember(2)] public long Sequence { get; set; }
        [ProtoMember(3)] public int ChunkX { get; set; }
        [ProtoMember(4)] public int ChunkY { get; set; }
        [ProtoMember(5)] public int ChunkZ { get; set; }
        [ProtoMember(6)] public long Revision { get; set; }
        [ProtoMember(7)] public int Applied { get; set; }
        [ProtoMember(8)] public int Deferred { get; set; }
        [ProtoMember(9)] public int Failed { get; set; }
        // Entries intentionally abandoned because the target chunk unloaded before apply.
        // These are not delivery failures and must not trigger an authoritative resync.
        [ProtoMember(10)] public int Skipped { get; set; }
    }

    [ProtoContract]
    public sealed class OreShieldSyncHelloPacket
    {
        [ProtoMember(1)] public int ProtocolVersion { get; set; } = NewfiesAntiCheat.Shared.OreShieldSyncProtocol.Version;
        [ProtoMember(2)] public string ClientVersion { get; set; } = "";
        [ProtoMember(3)] public string Capability { get; set; } = "";
        [ProtoMember(4)] public string BuildMarker { get; set; } = "";
    }

    [ProtoContract]
    public sealed class OreShieldChunkSyncRequestPacket
    {
        [ProtoMember(1)] public int ProtocolVersion { get; set; } = NewfiesAntiCheat.Shared.OreShieldSyncProtocol.Version;
        [ProtoMember(2)] public int ChunkX { get; set; }
        [ProtoMember(3)] public int ChunkY { get; set; }
        [ProtoMember(4)] public int ChunkZ { get; set; }
        [ProtoMember(5)] public long KnownRevision { get; set; }
    }
}
