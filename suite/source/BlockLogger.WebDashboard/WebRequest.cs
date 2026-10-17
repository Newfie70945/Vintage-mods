using System;
using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

internal sealed class WebRequest
{
	public string Method { get; init; } = "";

	public string Target { get; init; } = "";

	public string Path { get; init; } = "/";

	public Dictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

	public Dictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public string Body { get; init; } = "";
}
