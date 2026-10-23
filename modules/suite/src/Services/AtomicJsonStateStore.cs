using System.Text;
using Newtonsoft.Json;

namespace BlockLogger.Services;

// Large internal archives are streamed rather than materialized as a whole UTF-16
// JSON string. Only replace the previous file after serialization has succeeded.
internal static class AtomicJsonStateStore
{
    public static void Store<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384))
            using (var text = new StreamWriter(stream, new UTF8Encoding(false), 8192))
            using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented })
                JsonSerializer.CreateDefault().Serialize(writer, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
