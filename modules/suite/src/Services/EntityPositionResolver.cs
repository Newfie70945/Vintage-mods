using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace BlockLogger.Services;

// Entity position members have changed shape across game builds. Resolve once at runtime
// so a stale field/property reference cannot throw from a server event callback.
public static class EntityPositionResolver
{
    private static readonly Lazy<PositionAccessor> Accessor = new(BuildAccessor, true);

    public static string RuntimeMemberName => Accessor.Value.MemberName;

    public static bool TryGetCoordinates(Entity? entity, out double x, out double y, out double z, out int dimension)
    {
        x = y = z = 0;
        dimension = 0;
        if (entity == null) return false;

        try
        {
            return Accessor.Value.TryRead(entity, out x, out y, out z, out dimension, out _, out _);
        }
        catch
        {
            x = y = z = 0;
            dimension = 0;
            return false;
        }
    }

    public static bool TryGetPose(
        Entity? entity,
        out double x,
        out double y,
        out double z,
        out int dimension,
        out float yaw,
        out float pitch)
    {
        x = y = z = 0;
        dimension = 0;
        yaw = pitch = 0;
        if (entity == null) return false;

        try
        {
            return Accessor.Value.TryRead(entity, out x, out y, out z, out dimension, out yaw, out pitch);
        }
        catch
        {
            x = y = z = 0;
            dimension = 0;
            yaw = pitch = 0;
            return false;
        }
    }

    public static bool TrySetPose(Entity? entity, double x, double y, double z, float yaw, float pitch)
    {
        if (entity == null) return false;

        try
        {
            return Accessor.Value.TryWrite(entity, x, y, z, yaw, pitch);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetBlockPos(Entity? entity, out BlockPos pos)
    {
        if (TryGetCoordinates(entity, out var x, out var y, out var z, out var dimension))
        {
            pos = new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), dimension);
            return true;
        }

        pos = new BlockPos(0);
        return false;
    }

    public static BlockPos GetBlockPos(Entity? entity)
    {
        return TryGetBlockPos(entity, out var pos) ? pos : new BlockPos(0);
    }

    private static PositionAccessor BuildAccessor()
    {
        var entityType = typeof(Entity);

        foreach (var name in new[] { "Pos", "ServerPos", "SidedPos" })
        {
            var property = entityType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetMethod != null)
            {
                try
                {
                    var accessor = PositionAccessor.FromProperty(name, property);
                    if (accessor != null) return accessor;
                }
                catch { }
            }

            var field = entityType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    var accessor = PositionAccessor.FromField(name, field);
                    if (accessor != null) return accessor;
                }
                catch { }
            }
        }

        return PositionAccessor.Unavailable;
    }

    private sealed class PositionAccessor
    {
        public static readonly PositionAccessor Unavailable = new(
            "unavailable",
            _ => null,
            null, null, null, null, null, null,
            null, null, null, null, null, null);

        private readonly System.Func<object, object?> positionGetter;
        private readonly MemberReader? xReader;
        private readonly MemberReader? yReader;
        private readonly MemberReader? zReader;
        private readonly MemberReader? dimensionReader;
        private readonly MemberReader? yawReader;
        private readonly MemberReader? pitchReader;
        private readonly MethodInfo? setPosMethod;
        private readonly MemberWriter? xWriter;
        private readonly MemberWriter? yWriter;
        private readonly MemberWriter? zWriter;
        private readonly MemberWriter? yawWriter;
        private readonly MemberWriter? pitchWriter;

        private PositionAccessor(
            string memberName,
            System.Func<object, object?> positionGetter,
            MemberReader? xReader,
            MemberReader? yReader,
            MemberReader? zReader,
            MemberReader? dimensionReader,
            MemberReader? yawReader,
            MemberReader? pitchReader,
            MethodInfo? setPosMethod,
            MemberWriter? xWriter,
            MemberWriter? yWriter,
            MemberWriter? zWriter,
            MemberWriter? yawWriter,
            MemberWriter? pitchWriter)
        {
            MemberName = memberName;
            this.positionGetter = positionGetter;
            this.xReader = xReader;
            this.yReader = yReader;
            this.zReader = zReader;
            this.dimensionReader = dimensionReader;
            this.yawReader = yawReader;
            this.pitchReader = pitchReader;
            this.setPosMethod = setPosMethod;
            this.xWriter = xWriter;
            this.yWriter = yWriter;
            this.zWriter = zWriter;
            this.yawWriter = yawWriter;
            this.pitchWriter = pitchWriter;
        }

        public string MemberName { get; }

        public static PositionAccessor? FromProperty(string name, PropertyInfo property)
        {
            return Build(name, CompileGetter(property.DeclaringType!, property.PropertyType, property), property.PropertyType);
        }

        public static PositionAccessor? FromField(string name, FieldInfo field)
        {
            return Build(name, CompileGetter(field.DeclaringType!, field.FieldType, field), field.FieldType);
        }

        private static System.Func<object, object?> CompileGetter(Type declaringType, Type valueType, MemberInfo member)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            var cast = Expression.Convert(obj, declaringType);
            Expression access = member is PropertyInfo p
                ? Expression.Property(cast, p)
                : Expression.Field(cast, (FieldInfo)member);
            return Expression.Lambda<System.Func<object, object?>>(
                Expression.Convert(access, typeof(object)), obj).Compile();
        }

        private static PositionAccessor? Build(string name, System.Func<object, object?> getter, Type positionType)
        {
            var x = MemberReader.Find(positionType, "X");
            var y = MemberReader.Find(positionType, "Y");
            var z = MemberReader.Find(positionType, "Z");
            if (x == null || y == null || z == null) return null;

            var setPos = positionType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "SetPos", StringComparison.Ordinal)) return false;
                    var p = m.GetParameters();
                    return p.Length == 3 && p.All(a => IsNumeric(a.ParameterType));
                });

            return new PositionAccessor(
                name,
                getter,
                x,
                y,
                z,
                MemberReader.Find(positionType, "Dimension"),
                MemberReader.Find(positionType, "Yaw"),
                MemberReader.Find(positionType, "Pitch"),
                setPos,
                MemberWriter.Find(positionType, "X"),
                MemberWriter.Find(positionType, "Y"),
                MemberWriter.Find(positionType, "Z"),
                MemberWriter.Find(positionType, "Yaw"),
                MemberWriter.Find(positionType, "Pitch"));
        }

        public bool TryRead(
            object entity,
            out double x,
            out double y,
            out double z,
            out int dimension,
            out float yaw,
            out float pitch)
        {
            x = y = z = 0;
            dimension = 0;
            yaw = pitch = 0;
            if (xReader == null || yReader == null || zReader == null) return false;

            var position = positionGetter(entity);
            if (position == null) return false;

            if (!xReader.TryDouble(position, out x) ||
                !yReader.TryDouble(position, out y) ||
                !zReader.TryDouble(position, out z))
            {
                return false;
            }

            if (dimensionReader != null) dimensionReader.TryInt(position, out dimension);
            if (yawReader != null && yawReader.TryDouble(position, out var yawValue)) yaw = (float)yawValue;
            if (pitchReader != null && pitchReader.TryDouble(position, out var pitchValue)) pitch = (float)pitchValue;

            return !(double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) ||
                     double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z));
        }

        public bool TryWrite(object entity, double x, double y, double z, float yaw, float pitch)
        {
            var position = positionGetter(entity);
            if (position == null) return false;

            var positioned = false;

            if (setPosMethod != null)
            {
                try
                {
                    var parameters = setPosMethod.GetParameters();
                    setPosMethod.Invoke(position, new[]
                    {
                        ConvertNumeric(x, parameters[0].ParameterType),
                        ConvertNumeric(y, parameters[1].ParameterType),
                        ConvertNumeric(z, parameters[2].ParameterType)
                    });
                    positioned = true;
                }
                catch { }
            }

            if (!positioned && xWriter != null && yWriter != null && zWriter != null)
            {
                positioned =
                    xWriter.TryWrite(position, x) &&
                    yWriter.TryWrite(position, y) &&
                    zWriter.TryWrite(position, z);
            }

            if (!positioned) return false;

            yawWriter?.TryWrite(position, yaw);
            pitchWriter?.TryWrite(position, pitch);
            return true;
        }

        private static bool IsNumeric(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            return t == typeof(double) || t == typeof(float) ||
                   t == typeof(decimal) || t == typeof(int) ||
                   t == typeof(long) || t == typeof(short);
        }

        private static object ConvertNumeric(double value, Type targetType)
        {
            var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return Convert.ChangeType(value, t);
        }
    }

    private sealed class MemberReader
    {
        private readonly System.Func<object, object?> getter;

        private MemberReader(System.Func<object, object?> getter) => this.getter = getter;

        public static MemberReader? Find(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetMethod != null)
            {
                try { return new MemberReader(CompileMemberGetter(type, property)); }
                catch { return new MemberReader(obj => property.GetValue(obj)); }
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return new MemberReader(CompileMemberGetter(type, field)); }
                catch { return new MemberReader(obj => field.GetValue(obj)); }
            }

            return null;
        }

        private static System.Func<object, object?> CompileMemberGetter(Type declaringType, MemberInfo member)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            var cast = Expression.Convert(obj, declaringType);
            Expression access = member is PropertyInfo p
                ? Expression.Property(cast, p)
                : Expression.Field(cast, (FieldInfo)member);
            return Expression.Lambda<System.Func<object, object?>>(
                Expression.Convert(access, typeof(object)), obj).Compile();
        }

        public bool TryDouble(object obj, out double value)
        {
            value = 0;
            try
            {
                var raw = getter(obj);
                if (raw == null) return false;
                value = Convert.ToDouble(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryInt(object obj, out int value)
        {
            value = 0;
            try
            {
                var raw = getter(obj);
                if (raw == null) return false;
                value = Convert.ToInt32(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class MemberWriter
    {
        private readonly Action<object, object?> setter;
        private readonly Type valueType;

        private MemberWriter(Action<object, object?> setter, Type valueType)
        {
            this.setter = setter;
            this.valueType = valueType;
        }

        public static MemberWriter? Find(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.SetMethod != null)
            {
                return new MemberWriter((obj, value) => property.SetValue(obj, value), property.PropertyType);
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && !field.IsInitOnly)
            {
                return new MemberWriter((obj, value) => field.SetValue(obj, value), field.FieldType);
            }

            return null;
        }

        public bool TryWrite(object obj, double value)
        {
            try
            {
                var target = Nullable.GetUnderlyingType(valueType) ?? valueType;
                setter(obj, Convert.ChangeType(value, target));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
