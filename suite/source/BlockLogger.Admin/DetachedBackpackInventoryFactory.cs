using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;

namespace BlockLogger.Admin;

internal static class DetachedBackpackInventoryFactory
{
	private static readonly Lazy<ConstructorInfo?> Constructor = new Lazy<ConstructorInfo>(() => ResolveConstructor(AppDomain.CurrentDomain.GetAssemblies()));

	internal static InventoryBase Create(string inventoryId, ICoreAPI api)
	{
		ConstructorInfo constructorInfo = Constructor.Value ?? throw new NotSupportedException("The runtime backpack inventory layout is unavailable.");
		return (InventoryBase)constructorInfo.Invoke(new object[2] { inventoryId, api });
	}

	internal static ConstructorInfo? ResolveConstructor(IEnumerable<Assembly> assemblies)
	{
		Assembly[] array = assemblies.ToArray();
		string[] array2 = new string[2] { "Vintagestory.Common.InventoryPlayerBackpacks", "Vintagestory.GameContent.InventoryPlayerBackpacks" };
		foreach (string name in array2)
		{
			Assembly[] array3 = array;
			foreach (Assembly assembly in array3)
			{
				Type type = assembly.GetType(name, throwOnError: false);
				if (!(type == null) && !type.IsAbstract && typeof(InventoryBase).IsAssignableFrom(type))
				{
					ConstructorInfo constructor = type.GetConstructor(new Type[2]
					{
						typeof(string),
						typeof(ICoreAPI)
					});
					if (constructor != null)
					{
						return constructor;
					}
				}
			}
		}
		return null;
	}
}
