using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace BlockLogger.Compatibility;

public static class RuntimeCompatibility
{
	private sealed record RuntimeProbe(bool IsStratum, string StratumDetection);

	private static readonly Lazy<RuntimeProbe> probe = new Lazy<RuntimeProbe>(ProbeRuntime, isThreadSafe: true);

	public static bool IsStratum => probe.Value.IsStratum;

	public static string StratumDetection => probe.Value.StratumDetection;

	private static RuntimeProbe ProbeRuntime()
	{
		try
		{
			string text = "";
			string value = "";
			try
			{
				using Process process = Process.GetCurrentProcess();
				text = process.ProcessName ?? "";
				value = process.MainModule?.FileName ?? Environment.ProcessPath ?? "";
			}
			catch
			{
				value = Environment.ProcessPath ?? "";
			}
			if (ContainsStratum(text) || ContainsStratum(value))
			{
				return new RuntimeProbe(IsStratum: true, string.IsNullOrWhiteSpace(text) ? "server process path" : ("server process '" + text + "'"));
			}
			string text2 = Assembly.GetEntryAssembly()?.GetName().Name ?? "";
			if (ContainsStratum(text2))
			{
				return new RuntimeProbe(IsStratum: true, "entry assembly '" + text2 + "'");
			}
			string text3 = AppDomain.CurrentDomain.GetAssemblies().Select((Assembly a) =>
			{
				try
				{
					return a.GetName().Name ?? "";
				}
				catch
				{
					return "";
				}
			}).FirstOrDefault(ContainsStratum) ?? "";
			if (!string.IsNullOrWhiteSpace(text3))
			{
				return new RuntimeProbe(IsStratum: true, "loaded assembly '" + text3 + "'");
			}
		}
		catch
		{
		}
		return new RuntimeProbe(IsStratum: false, "not detected");
	}

	private static bool ContainsStratum(string? value)
	{
		return !string.IsNullOrWhiteSpace(value) && value.IndexOf("stratum", StringComparison.OrdinalIgnoreCase) >= 0;
	}
}
