using System;

namespace NewfiesCommunityMOTD;

public static class NewfiesMotdIntegration
{
	public const int ProtocolVersion = 1;

	public static INewfiesDiscordMotdProvider? Discord { get; private set; }

	public static INewfiesSuiteMotdProvider? Suite { get; private set; }

	public static event Action? ProvidersChanged;

	public static bool RegisterDiscord(INewfiesDiscordMotdProvider provider)
	{
		if (provider == null || provider.ProtocolVersion != 1)
		{
			return false;
		}
		Discord = provider;
		ProvidersChanged?.Invoke();
		return true;
	}

	public static bool RegisterSuite(INewfiesSuiteMotdProvider provider)
	{
		if (provider == null || provider.ProtocolVersion != 1)
		{
			return false;
		}
		Suite = provider;
		ProvidersChanged?.Invoke();
		return true;
	}

	public static void UnregisterDiscord(INewfiesDiscordMotdProvider provider)
	{
		if (Discord == provider)
		{
			Discord = null;
			ProvidersChanged?.Invoke();
		}
	}

	public static void UnregisterSuite(INewfiesSuiteMotdProvider provider)
	{
		if (Suite == provider)
		{
			Suite = null;
			ProvidersChanged?.Invoke();
		}
	}
}
