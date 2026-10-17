using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AnimalLimiterResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Message { get; set; } = "";

	[ProtoMember(4)]
	public List<string> Lines { get; set; } = new List<string>();

	[ProtoMember(5)]
	public bool Enabled { get; set; }

	[ProtoMember(6)]
	public int RadiusChunks { get; set; }

	[ProtoMember(7)]
	public int DefaultPerEntityLimit { get; set; }

	[ProtoMember(8)]
	public int PlayerProtectionRadiusBlocks { get; set; }

	[ProtoMember(9)]
	public int ReconciliationSeconds { get; set; }

	[ProtoMember(10)]
	public bool LimitAllEntities { get; set; }

	[ProtoMember(11)]
	public bool ProtectPets { get; set; }

	[ProtoMember(12)]
	public bool ProtectFarmAnimals { get; set; }

	[ProtoMember(13)]
	public bool ProtectNamedEntities { get; set; }

	[ProtoMember(14)]
	public bool KeepNearestToPlayers { get; set; }

	[ProtoMember(15)]
	public bool LogActions { get; set; }

	[ProtoMember(16)]
	public bool LogToCsv { get; set; }

	[ProtoMember(17)]
	public string GenerationFilter { get; set; } = "";
}
