using System;
using System.Reflection;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class PlayerDisconnectCompat
{
	private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	public static bool DisconnectWithReason(IServerPlayer player, string messageToOthers, string messageToPlayer, out string error)
	{
		error = "";
		if (player == null)
		{
			error = "player is null";
			return false;
		}
		if (messageToOthers == null)
		{
			messageToOthers = "Disconnected from server.";
		}
		if (messageToPlayer == null)
		{
			messageToPlayer = messageToOthers;
		}
		try
		{
			object memberValue = GetMemberValue(player, "client");
			object memberValue2 = GetMemberValue(player, "server");
			if (memberValue == null || memberValue2 == null)
			{
				error = "internal client/server handles unavailable";
				return false;
			}
			MethodInfo methodInfo = FindDisconnectPlayer(memberValue2.GetType(), memberValue.GetType());
			if (methodInfo == null)
			{
				error = "ServerMain.DisconnectPlayer(client,string,string) not found";
				return false;
			}
			methodInfo.Invoke(memberValue2, new object[3] { memberValue, messageToOthers, messageToPlayer });
			return true;
		}
		catch (TargetInvocationException ex)
		{
			error = ex.InnerException?.Message ?? ex.Message;
			return false;
		}
		catch (Exception ex2)
		{
			error = ex2.Message;
			return false;
		}
	}

	private static MethodInfo? FindDisconnectPlayer(Type serverType, Type clientType)
	{
		Type type = serverType;
		while (type != null)
		{
			MethodInfo[] methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (methodInfo.Name.Equals("DisconnectPlayer", StringComparison.Ordinal))
				{
					ParameterInfo[] parameters = methodInfo.GetParameters();
					if (parameters.Length == 3 && !(parameters[1].ParameterType != typeof(string)) && !(parameters[2].ParameterType != typeof(string)) && parameters[0].ParameterType.IsAssignableFrom(clientType))
					{
						return methodInfo;
					}
				}
			}
			type = type.BaseType;
		}
		return null;
	}

	private static object? GetMemberValue(object instance, string name)
	{
		Type type = instance.GetType();
		while (type != null)
		{
			FieldInfo field = type.GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				return field.GetValue(instance);
			}
			PropertyInfo property = type.GetProperty(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null && property.GetIndexParameters().Length == 0)
			{
				return property.GetValue(instance);
			}
			type = type.BaseType;
		}
		return null;
	}
}
