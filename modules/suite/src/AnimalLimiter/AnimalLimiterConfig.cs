using System.Collections.Generic;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterConfig
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    // Nullable only for backward-compatible loading of configs created before this setting existed.
    // Fresh installs are initialized disabled by AnimalLimiterSystem.LoadConfig().
    public bool? Enabled { get; set; }
    // If true, every eligible EntityAgent type is independently limited to DefaultPerEntityLimit.
    // If false, only codes matching Rules are limited.
    public bool LimitAllEntities { get; set; } = false;
    public int DefaultPerEntityLimit { get; set; } = 20;

    // Radius is measured in chunks. Radius 0 = one chunk, 1 = 3x3, 2 = 5x5, 3 = 7x7.
    public int RadiusChunks { get; set; } = 3;

    // How often automatic sweeps run. Keep this relatively slow on high-pop servers.
    public int SweepIntervalSeconds { get; set; } = 15;

    // Wildcards use '*'. Exact IDs and more-specific patterns win over broad patterns.
    public Dictionary<string, int> Rules { get; set; } = new()
    {
        // Example: ["spheniscidae:*"] = 20
    };

    // Optional per-rule radius override in chunks. Existing rules without an entry here
    // inherit RadiusChunks, preserving compatibility with older configs.
    public Dictionary<string, int> RuleRadiusChunks { get; set; } = new();

    // Never limit matching codes, even when LimitAllEntities is enabled.
    public List<string> ExcludeRules { get; set; } = new()
    {
        "game:player"
    };

    public bool ProtectPets { get; set; } = true;
    public bool ProtectFarmAnimals { get; set; } = true;
    public bool ProtectNamedEntities { get; set; } = true;

    // Off, WildOnly, BredOnly, ProtectBred
    // WildOnly: only entities that look generation 0/unbred are eligible.
    // BredOnly: only entities with generation > 0 are eligible.
    // ProtectBred: generation > 0 is always protected; wild entities may be limited.
    public string GenerationFilter { get; set; } = "ProtectBred";

    // Attribute keys can be extended for compatibility with other mods without recompiling.
    public List<string> OwnerAttributeKeys { get; set; } = new()
    {
        "owneruid", "ownerUid", "ownerUID", "owner", "masteruid", "masterUid"
    };

    public List<string> TamedAttributeKeys { get; set; } = new()
    {
        "tamed", "isTamed", "domesticated", "isDomesticated"
    };

    public List<string> NameAttributeKeys { get; set; } = new()
    {
        "name", "customName", "displayName", "petName"
    };

    public List<string> GenerationAttributeKeys { get; set; } = new()
    {
        "generation", "generationNumber", "gen", "breedGeneration"
    };

    public bool KeepNearestToPlayers { get; set; } = true;
    public int PlayerProtectionRadiusBlocks { get; set; } = 24;

    public bool LogActions { get; set; } = true;
    public bool LogToCsv { get; set; } = true;
    public string CsvDirName { get; set; } = "AnimalLimiterLogs";
    public string CsvFilePrefix { get; set; } = "animal-removals";

    public int RemovalHistoryDays { get; set; } = 7;
    public int MaxRemovalHistory { get; set; } = 5000;
}
