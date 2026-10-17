using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class ServerUtilitiesSelfTest
{
	public string BuildId { get; set; } = "";

	public string ModVersion { get; set; } = "";

	public string TargetGameVersion { get; set; } = "1.22.7";

	public long UnixMilliseconds { get; set; }

	public string Overall { get; set; } = "Unknown";

	public List<SelfTestCheck> Checks { get; set; } = new List<SelfTestCheck>();

	public List<FeatureCircuitBreakerStatus> CircuitBreakers { get; set; } = new List<FeatureCircuitBreakerStatus>();

	public List<FeatureOverheadSnapshot> Overhead { get; set; } = new List<FeatureOverheadSnapshot>();

	public List<WebRoutePolicyInfo> WebPolicies { get; set; } = new List<WebRoutePolicyInfo>();
}
