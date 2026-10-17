using System.Linq.Expressions;
using System.Reflection;

namespace BlockLogger.AntiCheat;

/// <summary>Compile optional packet-member access once, keeping reflection off the ingress path.</summary>
internal static class PacketSelectionIdReader<T>
{
    internal static readonly Func<T, string?> Read = CreateReader();

    private static Func<T, string?> CreateReader()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        var packet = Expression.Parameter(typeof(T), "packet");
        var field = typeof(T).GetField("SelectionBoxId", flags);
        if (field?.FieldType == typeof(string))
            return Expression.Lambda<Func<T, string?>>(Expression.Field(packet, field), packet).Compile();
        var property = typeof(T).GetProperty("SelectionBoxId", flags);
        if (property?.PropertyType == typeof(string) && property.GetMethod != null && property.GetIndexParameters().Length == 0)
            return Expression.Lambda<Func<T, string?>>(Expression.Property(packet, property), packet).Compile();
        return _ => null; // Older packet layouts have only SelectionBoxIndex.
    }
}
