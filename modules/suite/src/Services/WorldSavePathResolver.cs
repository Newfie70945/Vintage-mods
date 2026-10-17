namespace BlockLogger.Services;

/// <summary>Resolve only the active world database reported by the runtime, without opening it.</summary>
internal static class WorldSavePathResolver
{
    internal static string Resolve(object worldManager, string dataBasePath)
    {
        // CurrentWorldFilepath is the 1.22 API member and should already be an absolute
        // filename. Resolve by reflection so this source still compiles if Visual Studio
        // is accidentally pointed at an older VintagestoryAPI.dll. Older runtimes expose
        // CurrentWorldName, which may be a bare filename, Saves/<file>, or data/Saves/<file>.
        Type managerType = worldManager.GetType();
        var attempted = new List<string>();

        foreach (string propertyName in new[] { "CurrentWorldFilepath", "CurrentWorldName" })
        {
            string? value = null;
            var property = managerType.GetProperty(propertyName);
            if (property?.GetValue(worldManager) is string directValue)
            {
                value = directValue;
            }
            else
            {
                // Also handle an explicit interface implementation on the runtime type.
                foreach (Type iface in managerType.GetInterfaces())
                {
                    var interfaceProperty = iface.GetProperty(propertyName);
                    if (interfaceProperty?.GetValue(worldManager) is string interfaceValue)
                    {
                        value = interfaceValue;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(value)) continue;

            foreach (string candidate in BuildWorldPathCandidates(value, dataBasePath))
            {
                string fullCandidate;
                try
                {
                    fullCandidate = Path.GetFullPath(candidate);
                }
                catch
                {
                    continue;
                }

                if (!attempted.Contains(fullCandidate, StringComparer.OrdinalIgnoreCase))
                {
                    attempted.Add(fullCandidate);
                }

                if (File.Exists(fullCandidate))
                {
                    return fullCandidate;
                }
            }
        }

        string attemptedText = attempted.Count == 0
            ? "no candidate paths were produced"
            : string.Join("; ", attempted);

        throw new FileNotFoundException(
            "Unable to locate the active Vintage Story save database. " +
            $"Checked: {attemptedText}. DataBasePath='{dataBasePath}'.");
    }

    private static IEnumerable<string> BuildWorldPathCandidates(string rawValue, string dataBasePath)
    {
        string value = rawValue.Trim();
        if (value.Length == 0) yield break;

        // Always try exactly what the runtime supplied first. This is the correct path for
        // CurrentWorldFilepath on 1.22+ and also handles absolute legacy values.
        yield return value;
        if (!Path.HasExtension(value)) yield return value + ".vcdbs";

        if (Path.IsPathRooted(value)) yield break;

        string normalized = value.Replace('\\', '/').TrimStart('/');

        // Some older/runtime combinations return "data/Saves/default.vcdbs" even though
        // DataBasePath already points at ".../data". Strip the leading data/ in that case.
        const string dataPrefix = "data/";
        if (normalized.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relativeToData = normalized[dataPrefix.Length..];
            yield return Path.Combine(dataBasePath, relativeToData.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.HasExtension(relativeToData))
            {
                yield return Path.Combine(dataBasePath, (relativeToData + ".vcdbs").Replace('/', Path.DirectorySeparatorChar));
            }
        }

        // "Saves/default.vcdbs" should be relative to DataBasePath directly.
        if (normalized.StartsWith("Saves/", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(dataBasePath, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.HasExtension(normalized))
            {
                yield return Path.Combine(dataBasePath, (normalized + ".vcdbs").Replace('/', Path.DirectorySeparatorChar));
            }
        }

        // A bare save filename belongs in DataBasePath/Saves.
        string fileName = Path.GetFileName(normalized);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return Path.Combine(dataBasePath, "Saves", fileName);
            if (!Path.HasExtension(fileName))
            {
                yield return Path.Combine(dataBasePath, "Saves", fileName + ".vcdbs");
            }
        }
    }

}
