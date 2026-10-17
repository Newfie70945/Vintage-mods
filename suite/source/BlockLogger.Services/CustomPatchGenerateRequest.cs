using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class CustomPatchGenerateRequest
{
	public string Name { get; set; } = "";

	public string AssemblyName { get; set; } = "";

	public string TypeName { get; set; } = "";

	public string MethodName { get; set; } = "";

	public List<string> ParameterTypes { get; set; } = new List<string>();

	public string Mode { get; set; } = "Observe";

	public int ThrottleMilliseconds { get; set; } = 250;
}
