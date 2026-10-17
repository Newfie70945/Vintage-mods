using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace BlockLogger.Services;

public static class EntityPositionResolver
{
	private sealed class PositionAccessor
	{
		public static readonly PositionAccessor Unavailable = new PositionAccessor("unavailable", (object _) => (object?)null, null, null, null, null, null, null, null, null, null, null, null, null);

		private readonly Func<object, object?> positionGetter;

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

		public string MemberName { get; }

		private PositionAccessor(string memberName, Func<object, object?> positionGetter, MemberReader? xReader, MemberReader? yReader, MemberReader? zReader, MemberReader? dimensionReader, MemberReader? yawReader, MemberReader? pitchReader, MethodInfo? setPosMethod, MemberWriter? xWriter, MemberWriter? yWriter, MemberWriter? zWriter, MemberWriter? yawWriter, MemberWriter? pitchWriter)
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

		public static PositionAccessor? FromProperty(string name, PropertyInfo property)
		{
			return Build(name, CompileGetter(property.DeclaringType, property.PropertyType, property), property.PropertyType);
		}

		public static PositionAccessor? FromField(string name, FieldInfo field)
		{
			return Build(name, CompileGetter(field.DeclaringType, field.FieldType, field), field.FieldType);
		}

		private static Func<object, object?> CompileGetter(Type declaringType, Type valueType, MemberInfo member)
		{
			ParameterExpression parameterExpression = Expression.Parameter(typeof(object), "obj");
			UnaryExpression expression = Expression.Convert(parameterExpression, declaringType);
			Expression expression2 = ((member is PropertyInfo property) ? Expression.Property(expression, property) : Expression.Field(expression, (FieldInfo)member));
			return Expression.Lambda<Func<object, object>>(Expression.Convert(expression2, typeof(object)), new ParameterExpression[1] { parameterExpression }).Compile();
		}

		private static PositionAccessor? Build(string name, Func<object, object?> getter, Type positionType)
		{
			MemberReader memberReader = MemberReader.Find(positionType, "X");
			MemberReader memberReader2 = MemberReader.Find(positionType, "Y");
			MemberReader memberReader3 = MemberReader.Find(positionType, "Z");
			if (memberReader == null || memberReader2 == null || memberReader3 == null)
			{
				return null;
			}
			MethodInfo methodInfo = positionType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
			{
				if (!string.Equals(m.Name, "SetPos", StringComparison.Ordinal))
				{
					return false;
				}
				ParameterInfo[] parameters = m.GetParameters();
				return parameters.Length == 3 && parameters.All((ParameterInfo a) => IsNumeric(a.ParameterType));
			});
			return new PositionAccessor(name, getter, memberReader, memberReader2, memberReader3, MemberReader.Find(positionType, "Dimension"), MemberReader.Find(positionType, "Yaw"), MemberReader.Find(positionType, "Pitch"), methodInfo, MemberWriter.Find(positionType, "X"), MemberWriter.Find(positionType, "Y"), MemberWriter.Find(positionType, "Z"), MemberWriter.Find(positionType, "Yaw"), MemberWriter.Find(positionType, "Pitch"));
		}

		public bool TryRead(object entity, out double x, out double y, out double z, out int dimension, out float yaw, out float pitch)
		{
			x = (y = (z = 0.0));
			dimension = 0;
			yaw = (pitch = 0f);
			if (xReader == null || yReader == null || zReader == null)
			{
				return false;
			}
			object obj = positionGetter(entity);
			if (obj == null)
			{
				return false;
			}
			if (!xReader.TryDouble(obj, out x) || !yReader.TryDouble(obj, out y) || !zReader.TryDouble(obj, out z))
			{
				return false;
			}
			if (dimensionReader != null)
			{
				dimensionReader.TryInt(obj, out dimension);
			}
			if (yawReader != null && yawReader.TryDouble(obj, out var value))
			{
				yaw = (float)value;
			}
			if (pitchReader != null && pitchReader.TryDouble(obj, out var value2))
			{
				pitch = (float)value2;
			}
			return !double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z) && !double.IsInfinity(x) && !double.IsInfinity(y) && !double.IsInfinity(z);
		}

		public bool TryWrite(object entity, double x, double y, double z, float yaw, float pitch)
		{
			object obj = positionGetter(entity);
			if (obj == null)
			{
				return false;
			}
			bool flag = false;
			if (setPosMethod != null)
			{
				try
				{
					ParameterInfo[] parameters = setPosMethod.GetParameters();
					setPosMethod.Invoke(obj, new object[3]
					{
						ConvertNumeric(x, parameters[0].ParameterType),
						ConvertNumeric(y, parameters[1].ParameterType),
						ConvertNumeric(z, parameters[2].ParameterType)
					});
					flag = true;
				}
				catch
				{
				}
			}
			if (!flag && xWriter != null && yWriter != null && zWriter != null)
			{
				flag = xWriter.TryWrite(obj, x) && yWriter.TryWrite(obj, y) && zWriter.TryWrite(obj, z);
			}
			if (!flag)
			{
				return false;
			}
			yawWriter?.TryWrite(obj, yaw);
			pitchWriter?.TryWrite(obj, pitch);
			return true;
		}

		private static bool IsNumeric(Type type)
		{
			Type type2 = Nullable.GetUnderlyingType(type) ?? type;
			return type2 == typeof(double) || type2 == typeof(float) || type2 == typeof(decimal) || type2 == typeof(int) || type2 == typeof(long) || type2 == typeof(short);
		}

		private static object ConvertNumeric(double value, Type targetType)
		{
			Type conversionType = Nullable.GetUnderlyingType(targetType) ?? targetType;
			return Convert.ChangeType(value, conversionType);
		}
	}

	private sealed class MemberReader
	{
		private readonly Func<object, object?> getter;

		private MemberReader(Func<object, object?> getter)
		{
			this.getter = getter;
		}

		public static MemberReader? Find(Type type, string name)
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property?.GetMethod != null)
			{
				try
				{
					return new MemberReader(CompileMemberGetter(type, property));
				}
				catch
				{
					return new MemberReader((object obj3) => property.GetValue(obj3));
				}
			}
			FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				try
				{
					return new MemberReader(CompileMemberGetter(type, field));
				}
				catch
				{
					return new MemberReader((object obj3) => field.GetValue(obj3));
				}
			}
			return null;
		}

		private static Func<object, object?> CompileMemberGetter(Type declaringType, MemberInfo member)
		{
			ParameterExpression parameterExpression = Expression.Parameter(typeof(object), "obj");
			UnaryExpression expression = Expression.Convert(parameterExpression, declaringType);
			Expression expression2 = ((member is PropertyInfo property) ? Expression.Property(expression, property) : Expression.Field(expression, (FieldInfo)member));
			return Expression.Lambda<Func<object, object>>(Expression.Convert(expression2, typeof(object)), new ParameterExpression[1] { parameterExpression }).Compile();
		}

		public bool TryDouble(object obj, out double value)
		{
			value = 0.0;
			try
			{
				object obj2 = getter(obj);
				if (obj2 == null)
				{
					return false;
				}
				value = Convert.ToDouble(obj2);
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
				object obj2 = getter(obj);
				if (obj2 == null)
				{
					return false;
				}
				value = Convert.ToInt32(obj2);
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
			PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property?.SetMethod != null)
			{
				return new MemberWriter((object obj, object? value) =>
				{
					property.SetValue(obj, value);
				}, property.PropertyType);
			}
			FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null && !field.IsInitOnly)
			{
				return new MemberWriter((object obj, object? value) =>
				{
					field.SetValue(obj, value);
				}, field.FieldType);
			}
			return null;
		}

		public bool TryWrite(object obj, double value)
		{
			try
			{
				Type conversionType = Nullable.GetUnderlyingType(valueType) ?? valueType;
				setter(obj, Convert.ChangeType(value, conversionType));
				return true;
			}
			catch
			{
				return false;
			}
		}
	}

	private static readonly Lazy<PositionAccessor> Accessor = new Lazy<PositionAccessor>(BuildAccessor, isThreadSafe: true);

	public static string RuntimeMemberName => Accessor.Value.MemberName;

	public static bool TryGetCoordinates(Entity? entity, out double x, out double y, out double z, out int dimension)
	{
		x = (y = (z = 0.0));
		dimension = 0;
		if (entity == null)
		{
			return false;
		}
		try
		{
			float yaw;
			float pitch;
			return Accessor.Value.TryRead(entity, out x, out y, out z, out dimension, out yaw, out pitch);
		}
		catch
		{
			x = (y = (z = 0.0));
			dimension = 0;
			return false;
		}
	}

	public static bool TryGetPose(Entity? entity, out double x, out double y, out double z, out int dimension, out float yaw, out float pitch)
	{
		x = (y = (z = 0.0));
		dimension = 0;
		yaw = (pitch = 0f);
		if (entity == null)
		{
			return false;
		}
		try
		{
			return Accessor.Value.TryRead(entity, out x, out y, out z, out dimension, out yaw, out pitch);
		}
		catch
		{
			x = (y = (z = 0.0));
			dimension = 0;
			yaw = (pitch = 0f);
			return false;
		}
	}

	public static bool TrySetPose(Entity? entity, double x, double y, double z, float yaw, float pitch)
	{
		if (entity == null)
		{
			return false;
		}
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
		BlockPos pos;
		return TryGetBlockPos(entity, out pos) ? pos : new BlockPos(0);
	}

	private static PositionAccessor BuildAccessor()
	{
		Type typeFromHandle = typeof(Entity);
		string[] array = new string[3] { "Pos", "ServerPos", "SidedPos" };
		foreach (string name in array)
		{
			PropertyInfo property = typeFromHandle.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property?.GetMethod != null)
			{
				try
				{
					PositionAccessor positionAccessor = PositionAccessor.FromProperty(name, property);
					if (positionAccessor != null)
					{
						return positionAccessor;
					}
				}
				catch
				{
				}
			}
			FieldInfo field = typeFromHandle.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (!(field != null))
			{
				continue;
			}
			try
			{
				PositionAccessor positionAccessor2 = PositionAccessor.FromField(name, field);
				if (positionAccessor2 != null)
				{
					return positionAccessor2;
				}
			}
			catch
			{
			}
		}
		return PositionAccessor.Unavailable;
	}
}
