using System;

namespace BlockLogger.Services;

public sealed class ConfigMigrationException : Exception
{
	public string ConfigFileName { get; }

	public ConfigMigrationException(string configFileName, string message, Exception? innerException = null)
		: base(message, innerException)
	{
		ConfigFileName = configFileName;
	}
}
