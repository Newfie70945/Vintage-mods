using System;
using System.Linq.Expressions;
using System.Reflection;

namespace BlockLogger.AntiCheat;

internal static class PacketSelectionIdReader<T>
{
	internal static readonly Func<T, string?> Read = CreateReader();

	private static Func<T, string?> CreateReader()
	{
		ParameterExpression parameterExpression = Expression.Parameter(typeof(T), "packet");
		FieldInfo field = typeof(T).GetField("SelectionBoxId", BindingFlags.Instance | BindingFlags.Public);
		if (field?.FieldType == typeof(string))
		{
			return Expression.Lambda<Func<T, string>>(Expression.Field(parameterExpression, field), new ParameterExpression[1] { parameterExpression }).Compile();
		}
		PropertyInfo property = typeof(T).GetProperty("SelectionBoxId", BindingFlags.Instance | BindingFlags.Public);
		if (property?.PropertyType == typeof(string) && property.GetMethod != null && property.GetIndexParameters().Length == 0)
		{
			return Expression.Lambda<Func<T, string>>(Expression.Property(parameterExpression, property), new ParameterExpression[1] { parameterExpression }).Compile();
		}
		return (T _) => (string?)null;
	}
}
