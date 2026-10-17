using System.Reflection;
using Vintagestory.API.Common;

namespace BlockLogger.Admin;

/// <summary>Resolve the runtime's detached backpack implementation across engine namespaces.</summary>
internal static class DetachedBackpackInventoryFactory
{
    private static readonly Lazy<ConstructorInfo?> Constructor = new(() => ResolveConstructor(AppDomain.CurrentDomain.GetAssemblies()));

    internal static InventoryBase Create(string inventoryId, ICoreAPI api)
    {
        var constructor = Constructor.Value ?? throw new NotSupportedException("The runtime backpack inventory layout is unavailable.");
        return (InventoryBase)constructor.Invoke(new object[] { inventoryId, api });
    }

    internal static ConstructorInfo? ResolveConstructor(IEnumerable<Assembly> assemblies)
    {
        var loaded = assemblies.ToArray();
        foreach (string name in new[] { "Vintagestory.Common.InventoryPlayerBackpacks", "Vintagestory.GameContent.InventoryPlayerBackpacks" })
        {
            foreach (var assembly in loaded)
            {
                var type = assembly.GetType(name, throwOnError: false);
                if (type == null || type.IsAbstract || !typeof(InventoryBase).IsAssignableFrom(type)) continue;
                var constructor = type.GetConstructor(new[] { typeof(string), typeof(ICoreAPI) });
                if (constructor != null) return constructor;
            }
        }
        return null;
    }
}
