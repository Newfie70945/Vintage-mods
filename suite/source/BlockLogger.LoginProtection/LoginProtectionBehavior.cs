using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace BlockLogger.LoginProtection;

public sealed class LoginProtectionBehavior : EntityBehavior
{
	private readonly LoginProtectionSystem system;

	public LoginProtectionBehavior(Entity entity, LoginProtectionSystem system)
		: base(entity)
	{
		this.system = system;
	}

	public override string PropertyName()
	{
		return "newfiesloginprotection";
	}

	public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
	{
		if (!(damage <= 0f) && entity is EntityPlayer target && system.ShouldBlockIncomingDamage(target, damageSource))
		{
			damage = 0f;
		}
	}

	public override void DidAttack(DamageSource source, EntityAgent targetEntity, ref EnumHandling handled)
	{
		if (entity is EntityPlayer attacker)
		{
			system.OnProtectedPlayerAttacked(attacker);
		}
	}
}
