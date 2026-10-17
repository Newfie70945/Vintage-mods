using System;

namespace NewfiesCommunityMOTD;

public interface INewfiesSuiteMotdProvider
{
	int ProtocolVersion { get; }

	bool IsAvailable { get; }

	event Action? StatusChanged;

	string GetPlayerFacingStatusVtml();
}
