namespace BlockLogger.Models;

public enum LogActionType
{
    Unknown = 0,
    BlockBreak = 1,
    BlockPlace = 2,
    BlockUse = 3,
    ContainerOpen = 4,
    ContainerTake = 5,
    ContainerPut = 6,
    EntityInteract = 7,
    EntitySpawn = 8,
    EntityDeath = 9,
    EntityDespawn = 10
}
