using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Services;

public sealed class CustomPatchDefinition
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public bool Enabled { get; set; }

	public string AssemblyName { get; set; } = "";

	public string TypeName { get; set; } = "";

	public string MethodName { get; set; } = "";

	public List<string> ParameterTypes { get; set; } = new List<string>();

	public string Mode { get; set; } = "Observe";

	public int ThrottleMilliseconds { get; set; } = 250;

	public string Notes { get; set; } = "";

	public string RuntimeStatus { get; set; } = "Disabled";

	public string RuntimeMessage { get; set; } = "";

	public void Clamp()
	{
		Id = (Id ?? "").Trim();
		Name = (Name ?? "").Trim();
		AssemblyName = (AssemblyName ?? "").Trim();
		TypeName = (TypeName ?? "").Trim();
		MethodName = (MethodName ?? "").Trim();
		Mode = (string.IsNullOrWhiteSpace(Mode) ? "Observe" : Mode.Trim());
		ThrottleMilliseconds = Math.Clamp(ThrottleMilliseconds, 50, 60000);
		Notes = (Notes ?? "").Trim();
		if (ParameterTypes == null)
		{
			List<string> list = (ParameterTypes = new List<string>());
		}
		ParameterTypes = (from x in ParameterTypes
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Take(16).ToList();
	}

	public CustomPatchDefinition Clone()
	{
		return new CustomPatchDefinition
		{
			Id = Id,
			Name = Name,
			Enabled = Enabled,
			AssemblyName = AssemblyName,
			TypeName = TypeName,
			MethodName = MethodName,
			ParameterTypes = new List<string>(ParameterTypes),
			Mode = Mode,
			ThrottleMilliseconds = ThrottleMilliseconds,
			Notes = Notes,
			RuntimeStatus = RuntimeStatus,
			RuntimeMessage = RuntimeMessage
		};
	}
}
