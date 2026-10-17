using System;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace BlockLogger.Services;

public static class EntityRuntimeAccessors
{
	private static readonly System.Func<Entity, long> EntityIdGetter = BuildValueGetter<Entity, long>("EntityId", -1L);

	private static readonly System.Func<Entity, EntityProperties?> PropertiesGetter = BuildReferenceGetter<Entity, EntityProperties>("Properties");

	private static readonly System.Func<EntityProperties, int> PropertiesIdGetter = BuildValueGetter<EntityProperties, int>("Id", -1);

	private static readonly System.Func<EntityProperties, AssetLocation?> PropertiesCodeGetter = BuildReferenceGetter<EntityProperties, AssetLocation>("Code");

	private static readonly System.Func<EntityProperties, string?> PropertiesClassGetter = BuildReferenceGetter<EntityProperties, string>("Class");

	private static readonly System.Func<DamageSource, Entity?> SourceEntityGetter = BuildReferenceGetter<DamageSource, Entity>("SourceEntity");

	private static readonly System.Func<DamageSource, Vec3d?> SourcePosGetter = BuildReferenceGetter<DamageSource, Vec3d>("SourcePos");

	private static readonly System.Func<DamageSource, int> DamageTierGetter = BuildValueGetter<DamageSource, int>("DamageTier", int.MinValue);

	private static readonly System.Func<DamageSource, object?> DamageTypeGetter = BuildObjectGetter<DamageSource>("Type");

	private static readonly System.Func<EntityDespawnData, object?> DespawnReasonGetter = BuildObjectGetter<EntityDespawnData>("Reason");

	private static readonly System.Func<IPlayer, Entity?> PlayerEntityGetter = BuildReferenceGetter<IPlayer, Entity>("Entity");

	public static long GetEntityId(Entity? entity)
	{
		if (entity == null)
		{
			return -1L;
		}
		try
		{
			return EntityIdGetter(entity);
		}
		catch
		{
			return -1L;
		}
	}

	public static EntityProperties? GetProperties(Entity? entity)
	{
		if (entity == null)
		{
			return null;
		}
		try
		{
			return PropertiesGetter(entity);
		}
		catch
		{
			return null;
		}
	}

	public static int GetPropertiesId(EntityProperties? properties)
	{
		if (properties == null)
		{
			return -1;
		}
		try
		{
			return PropertiesIdGetter(properties);
		}
		catch
		{
			return -1;
		}
	}

	public static AssetLocation? GetPropertiesCode(EntityProperties? properties)
	{
		if (properties == null)
		{
			return null;
		}
		try
		{
			return PropertiesCodeGetter(properties);
		}
		catch
		{
			return null;
		}
	}

	public static string? GetPropertiesClass(EntityProperties? properties)
	{
		if (properties == null)
		{
			return null;
		}
		try
		{
			return PropertiesClassGetter(properties);
		}
		catch
		{
			return null;
		}
	}

	public static Entity? GetDamageSourceEntity(DamageSource? source)
	{
		if (source == null)
		{
			return null;
		}
		try
		{
			return SourceEntityGetter(source);
		}
		catch
		{
			return null;
		}
	}

	public static Entity? GetDamageCauseEntity(DamageSource? source)
	{
		if (source == null)
		{
			return null;
		}
		try
		{
			return source.GetCauseEntity() ?? SourceEntityGetter(source);
		}
		catch
		{
			try
			{
				return SourceEntityGetter(source);
			}
			catch
			{
				return null;
			}
		}
	}

	public static Vec3d? GetDamageSourcePosition(DamageSource? source)
	{
		if (source == null)
		{
			return null;
		}
		try
		{
			return source.GetSourcePosition() ?? SourcePosGetter(source);
		}
		catch
		{
			try
			{
				return SourcePosGetter(source);
			}
			catch
			{
				return null;
			}
		}
	}

	public static int GetDamageTier(DamageSource? source)
	{
		if (source == null)
		{
			return int.MinValue;
		}
		try
		{
			return DamageTierGetter(source);
		}
		catch
		{
			return int.MinValue;
		}
	}

	public static string? GetDamageType(DamageSource? source)
	{
		if (source == null)
		{
			return null;
		}
		try
		{
			return DamageTypeGetter(source)?.ToString();
		}
		catch
		{
			return null;
		}
	}

	public static Entity? GetPlayerEntity(IPlayer? player)
	{
		if (player == null)
		{
			return null;
		}
		try
		{
			return PlayerEntityGetter(player);
		}
		catch
		{
			return null;
		}
	}

	public static string? GetDespawnReason(EntityDespawnData? data)
	{
		if (data == null)
		{
			return null;
		}
		try
		{
			return DespawnReasonGetter(data)?.ToString();
		}
		catch
		{
			return null;
		}
	}

	private static System.Func<TTarget, TValue> BuildValueGetter<TTarget, TValue>(string memberName, TValue fallback) where TTarget : class where TValue : struct
	{
		MemberInfo member = FindMember(typeof(TTarget), memberName);
		if (member == null)
		{
			return (TTarget _) => fallback;
		}
		try
		{
			ParameterExpression parameterExpression = Expression.Parameter(typeof(TTarget), "target");
			if (1 == 0)
			{
			}
			MemberExpression memberExpression;
			if (!(member is PropertyInfo property))
			{
				if (!(member is FieldInfo field))
				{
					throw new InvalidOperationException();
				}
				memberExpression = Expression.Field(parameterExpression, field);
			}
			else
			{
				memberExpression = Expression.Property(parameterExpression, property);
			}
			if (1 == 0)
			{
			}
			Expression expression = memberExpression;
			if (expression.Type != typeof(TValue))
			{
				expression = Expression.Convert(expression, typeof(TValue));
			}
			return Expression.Lambda<System.Func<TTarget, TValue>>(expression, new ParameterExpression[1] { parameterExpression }).Compile();
		}
		catch
		{
			return (TTarget target) =>
			{
				try
				{
					return (ReadMember(member, target) is TValue val) ? val : fallback;
				}
				catch
				{
					return fallback;
				}
			};
		}
	}

	private static System.Func<TTarget, TValue?> BuildReferenceGetter<TTarget, TValue>(string memberName) where TTarget : class where TValue : class
	{
		MemberInfo member = FindMember(typeof(TTarget), memberName);
		if (member == null)
		{
			return (TTarget _) => (TValue?)null;
		}
		try
		{
			ParameterExpression parameterExpression = Expression.Parameter(typeof(TTarget), "target");
			if (1 == 0)
			{
			}
			MemberExpression memberExpression;
			if (!(member is PropertyInfo property))
			{
				if (!(member is FieldInfo field))
				{
					throw new InvalidOperationException();
				}
				memberExpression = Expression.Field(parameterExpression, field);
			}
			else
			{
				memberExpression = Expression.Property(parameterExpression, property);
			}
			if (1 == 0)
			{
			}
			Expression expression = memberExpression;
			if (expression.Type != typeof(TValue))
			{
				expression = Expression.Convert(expression, typeof(TValue));
			}
			return Expression.Lambda<System.Func<TTarget, TValue>>(expression, new ParameterExpression[1] { parameterExpression }).Compile();
		}
		catch
		{
			return (TTarget target) =>
			{
				try
				{
					return ReadMember(member, target) as TValue;
				}
				catch
				{
					return (TValue?)null;
				}
			};
		}
	}

	private static System.Func<TTarget, object?> BuildObjectGetter<TTarget>(string memberName) where TTarget : class
	{
		MemberInfo member = FindMember(typeof(TTarget), memberName);
		if (member == null)
		{
			return (TTarget _) => (object?)null;
		}
		try
		{
			ParameterExpression parameterExpression = Expression.Parameter(typeof(TTarget), "target");
			if (1 == 0)
			{
			}
			MemberExpression memberExpression;
			if (!(member is PropertyInfo property))
			{
				if (!(member is FieldInfo field))
				{
					throw new InvalidOperationException();
				}
				memberExpression = Expression.Field(parameterExpression, field);
			}
			else
			{
				memberExpression = Expression.Property(parameterExpression, property);
			}
			if (1 == 0)
			{
			}
			Expression expression = memberExpression;
			UnaryExpression body = Expression.Convert(expression, typeof(object));
			return Expression.Lambda<System.Func<TTarget, object>>(body, new ParameterExpression[1] { parameterExpression }).Compile();
		}
		catch
		{
			return (TTarget target) =>
			{
				try
				{
					return ReadMember(member, target);
				}
				catch
				{
					return (object?)null;
				}
			};
		}
	}

	private static MemberInfo? FindMember(Type type, string name)
	{
		return (MemberInfo?)(((object)type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) ?? ((object)type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)));
	}

	private static object? ReadMember(MemberInfo member, object target)
	{
		if (1 == 0)
		{
		}
		object result;
		if (member is PropertyInfo propertyInfo)
		{
			result = propertyInfo.GetValue(target);
		}
		else
		{
			result = ((!(member is FieldInfo fieldInfo)) ? null : fieldInfo.GetValue(target));
		}
		if (1 == 0)
		{
		}
		return result;
	}
}
