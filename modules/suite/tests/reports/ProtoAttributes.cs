namespace ProtoBuf;
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ProtoContractAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ProtoMemberAttribute(int number) : Attribute { public int Number { get; } = number; }
