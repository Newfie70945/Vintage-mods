namespace BlockLogger.Models;

public enum LogActionType
{
	Unknown,
	BlockBreak,
	BlockPlace,
	BlockUse,
	ContainerOpen,
	ContainerTake,
	ContainerPut,
	EntityInteract,
	EntitySpawn,
	EntityDeath,
	EntityDespawn
}
