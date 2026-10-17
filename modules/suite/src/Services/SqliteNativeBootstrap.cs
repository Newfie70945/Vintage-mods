using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BlockLogger.Services;

/// <summary>Vintage Story permits nested unmanaged DLLs only beneath native/.</summary>
internal static class SqliteNativeBootstrap
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "The game loads this mod library directly; native SQLite resolution must be installed before its first provider use.")]
    internal static void Initialize()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A shared provider assembly can already have a resolver owned by another mod.
            // The runtime permits one resolver per assembly; preserve that owner's resolver.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "e_sqlite3") return IntPtr.Zero;
        string file = OperatingSystem.IsWindows() ? "e_sqlite3.dll" :
            OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so";
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string fallbackRid = os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string root = Path.GetDirectoryName(typeof(SqliteNativeBootstrap).Assembly.Location)!;
        foreach (string rid in new[] { RuntimeInformation.RuntimeIdentifier, fallbackRid }.Distinct())
        {
            // The second path supports unmodified SDK output during developer builds.
            foreach (string folder in new[] { Path.Combine("native", "runtimes"), "runtimes" })
            {
                string candidate = Path.Combine(root, folder, rid, "native", file);
                if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
            }
        }
        return IntPtr.Zero;
    }
}
