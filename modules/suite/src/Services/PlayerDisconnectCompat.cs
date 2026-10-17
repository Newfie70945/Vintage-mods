using System;
using System.Reflection;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

/// <summary>
/// Compatibility helper for Vintage Story 1.22.x disconnect reasons.
/// IServerPlayer.Disconnect(string) currently routes its argument as the
/// message for other players on some builds while the kicked client receives
/// a null/unknown reason. The internal ServerMain.DisconnectPlayer method
/// accepts both strings and sends the player-facing reason correctly.
/// </summary>
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

        messageToOthers ??= "Disconnected from server.";
        messageToPlayer ??= messageToOthers;

        try
        {
            object? client = GetMemberValue(player, "client");
            object? server = GetMemberValue(player, "server");
            if (client == null || server == null)
            {
                error = "internal client/server handles unavailable";
                return false;
            }

            MethodInfo? disconnectPlayer = FindDisconnectPlayer(server.GetType(), client.GetType());
            if (disconnectPlayer == null)
            {
                error = "ServerMain.DisconnectPlayer(client,string,string) not found";
                return false;
            }

            disconnectPlayer.Invoke(server, new[] { client, (object)messageToOthers, messageToPlayer });
            return true;
        }
        catch (TargetInvocationException ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static MethodInfo? FindDisconnectPlayer(Type serverType, Type clientType)
    {
        for (Type? type = serverType; type != null; type = type.BaseType)
        {
            foreach (MethodInfo method in type.GetMethods(InstanceFlags | BindingFlags.DeclaredOnly))
            {
                if (!method.Name.Equals("DisconnectPlayer", StringComparison.Ordinal)) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 3) continue;
                if (parameters[1].ParameterType != typeof(string) || parameters[2].ParameterType != typeof(string)) continue;
                if (!parameters[0].ParameterType.IsAssignableFrom(clientType)) continue;
                return method;
            }
        }

        return null;
    }

    private static object? GetMemberValue(object instance, string name)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);

            PropertyInfo? property = type.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(instance);
        }

        return null;
    }
}
