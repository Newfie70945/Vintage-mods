using System;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace BlockLogger.Services;

// Runtime-bound accessors for entity API members that vary between game builds.
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
        if (entity == null) return -1;
        try { return EntityIdGetter(entity); } catch { return -1; }
    }

    public static EntityProperties? GetProperties(Entity? entity)
    {
        if (entity == null) return null;
        try { return PropertiesGetter(entity); } catch { return null; }
    }

    public static int GetPropertiesId(EntityProperties? properties)
    {
        if (properties == null) return -1;
        try { return PropertiesIdGetter(properties); } catch { return -1; }
    }

    public static AssetLocation? GetPropertiesCode(EntityProperties? properties)
    {
        if (properties == null) return null;
        try { return PropertiesCodeGetter(properties); } catch { return null; }
    }

    public static string? GetPropertiesClass(EntityProperties? properties)
    {
        if (properties == null) return null;
        try { return PropertiesClassGetter(properties); } catch { return null; }
    }

    public static Entity? GetDamageSourceEntity(DamageSource? source)
    {
        if (source == null) return null;
        try { return SourceEntityGetter(source); } catch { return null; }
    }

    public static Entity? GetDamageCauseEntity(DamageSource? source)
    {
        if (source == null) return null;
        try { return source.GetCauseEntity() ?? SourceEntityGetter(source); }
        catch
        {
            try { return SourceEntityGetter(source); } catch { return null; }
        }
    }

    public static Vec3d? GetDamageSourcePosition(DamageSource? source)
    {
        if (source == null) return null;
        try { return source.GetSourcePosition() ?? SourcePosGetter(source); }
        catch
        {
            try { return SourcePosGetter(source); } catch { return null; }
        }
    }

    public static int GetDamageTier(DamageSource? source)
    {
        if (source == null) return int.MinValue;
        try { return DamageTierGetter(source); } catch { return int.MinValue; }
    }

    public static string? GetDamageType(DamageSource? source)
    {
        if (source == null) return null;
        try { return DamageTypeGetter(source)?.ToString(); } catch { return null; }
    }

    public static Entity? GetPlayerEntity(IPlayer? player)
    {
        if (player == null) return null;
        try { return PlayerEntityGetter(player); } catch { return null; }
    }

    public static string? GetDespawnReason(EntityDespawnData? data)
    {
        if (data == null) return null;
        try { return DespawnReasonGetter(data)?.ToString(); } catch { return null; }
    }

    private static System.Func<TTarget, TValue> BuildValueGetter<TTarget, TValue>(string memberName, TValue fallback)
        where TTarget : class
        where TValue : struct
    {
        var member = FindMember(typeof(TTarget), memberName);
        if (member == null) return _ => fallback;

        try
        {
            var target = Expression.Parameter(typeof(TTarget), "target");
            Expression access = member switch
            {
                PropertyInfo p => Expression.Property(target, p),
                FieldInfo f => Expression.Field(target, f),
                _ => throw new InvalidOperationException()
            };

            if (access.Type != typeof(TValue)) access = Expression.Convert(access, typeof(TValue));
            return Expression.Lambda<System.Func<TTarget, TValue>>(access, target).Compile();
        }
        catch
        {
            return target =>
            {
                try
                {
                    var value = ReadMember(member, target);
                    return value is TValue typed ? typed : fallback;
                }
                catch { return fallback; }
            };
        }
    }

    private static System.Func<TTarget, TValue?> BuildReferenceGetter<TTarget, TValue>(string memberName)
        where TTarget : class
        where TValue : class
    {
        var member = FindMember(typeof(TTarget), memberName);
        if (member == null) return _ => null;

        try
        {
            var target = Expression.Parameter(typeof(TTarget), "target");
            Expression access = member switch
            {
                PropertyInfo p => Expression.Property(target, p),
                FieldInfo f => Expression.Field(target, f),
                _ => throw new InvalidOperationException()
            };

            if (access.Type != typeof(TValue)) access = Expression.Convert(access, typeof(TValue));
            return Expression.Lambda<System.Func<TTarget, TValue?>>(access, target).Compile();
        }
        catch
        {
            return target =>
            {
                try { return ReadMember(member, target) as TValue; }
                catch { return null; }
            };
        }
    }

    private static System.Func<TTarget, object?> BuildObjectGetter<TTarget>(string memberName)
        where TTarget : class
    {
        var member = FindMember(typeof(TTarget), memberName);
        if (member == null) return _ => null;

        try
        {
            var target = Expression.Parameter(typeof(TTarget), "target");
            Expression access = member switch
            {
                PropertyInfo p => Expression.Property(target, p),
                FieldInfo f => Expression.Field(target, f),
                _ => throw new InvalidOperationException()
            };
            var boxed = Expression.Convert(access, typeof(object));
            return Expression.Lambda<System.Func<TTarget, object?>>(boxed, target).Compile();
        }
        catch
        {
            return target =>
            {
                try { return ReadMember(member, target); }
                catch { return null; }
            };
        }
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return (MemberInfo?)type.GetProperty(name, flags) ?? type.GetField(name, flags);
    }

    private static object? ReadMember(MemberInfo member, object target) => member switch
    {
        PropertyInfo p => p.GetValue(target),
        FieldInfo f => f.GetValue(target),
        _ => null
    };
}
