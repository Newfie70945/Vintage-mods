using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SQLitePCL;

namespace BlockLogger.Services;

internal static class SqliteNativeBootstrap
{
	[ModuleInitializer]
	internal static void Initialize()
	{
		try
		{
			NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_e_sqlite3).Assembly, Resolve);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (name != "e_sqlite3")
		{
			return IntPtr.Zero;
		}
		string text;
		if (OperatingSystem.IsWindows())
		{
			text = "e_sqlite3.dll";
		}
		else
		{
			text = (OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so");
		}
		string text2;
		if (OperatingSystem.IsWindows())
		{
			text2 = "win";
		}
		else
		{
			text2 = (OperatingSystem.IsMacOS() ? "osx" : "linux");
		}
		string text3 = text2 + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
		string directoryName = Path.GetDirectoryName(typeof(SqliteNativeBootstrap).Assembly.Location);
		foreach (string item in new string[2]
		{
			RuntimeInformation.RuntimeIdentifier,
			text3
		}.Distinct())
		{
			string[] array = new string[2]
			{
				Path.Combine("native", "runtimes"),
				"runtimes"
			};
			foreach (string text4 in array)
			{
				InlineArray5<string> buffer = default;
				buffer[0] = directoryName;
				buffer[1] = text4;
				buffer[2] = item;
				buffer[3] = "native";
				buffer[4] = text;
				string text5 = Path.Combine(buffer);
				if (File.Exists(text5))
				{
					return NativeLibrary.Load(text5);
				}
			}
		}
		return IntPtr.Zero;
	}
}
