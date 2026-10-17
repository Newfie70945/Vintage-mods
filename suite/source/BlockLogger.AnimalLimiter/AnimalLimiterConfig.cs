using System.Collections.Generic;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterConfig
{
	public const int CurrentSchemaVersion = 2;

	public int SchemaVersion { get; set; } = 2;

	public bool? Enabled { get; set; }

	public bool LimitAllEntities { get; set; } = false;

	public int DefaultPerEntityLimit { get; set; } = 20;

	public int RadiusChunks { get; set; } = 3;

	public int SweepIntervalSeconds { get; set; } = 15;

	public Dictionary<string, int> Rules { get; set; } = new Dictionary<string, int>();

	public Dictionary<string, int> RuleRadiusChunks { get; set; } = new Dictionary<string, int>();

	public List<string> ExcludeRules { get; set; } = new List<string> { "game:player" };

	public bool ProtectPets { get; set; } = true;

	public bool ProtectFarmAnimals { get; set; } = true;

	public bool ProtectNamedEntities { get; set; } = true;

	public string GenerationFilter { get; set; } = "ProtectBred";

	public List<string> OwnerAttributeKeys { get; set; } = new List<string> { "owneruid", "ownerUid", "ownerUID", "owner", "masteruid", "masterUid" };

	public List<string> TamedAttributeKeys { get; set; } = new List<string> { "tamed", "isTamed", "domesticated", "isDomesticated" };

	public List<string> NameAttributeKeys { get; set; } = new List<string> { "name", "customName", "displayName", "petName" };

	public List<string> GenerationAttributeKeys { get; set; } = new List<string> { "generation", "generationNumber", "gen", "breedGeneration" };

	public bool KeepNearestToPlayers { get; set; } = true;

	public int PlayerProtectionRadiusBlocks { get; set; } = 24;

	public bool LogActions { get; set; } = true;

	public bool LogToCsv { get; set; } = true;

	public string CsvDirName { get; set; } = "AnimalLimiterLogs";

	public string CsvFilePrefix { get; set; } = "animal-removals";

	public int RemovalHistoryDays { get; set; } = 7;

	public int MaxRemovalHistory { get; set; } = 5000;
}
