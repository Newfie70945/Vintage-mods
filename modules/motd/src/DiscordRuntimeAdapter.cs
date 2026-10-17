using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NewfiesCommunityMOTD
{
    // Optional runtime bridge: Community MOTD discovers Discord 1.0.8 without either
    // project requiring a compile-time ProjectReference to the other.
    internal sealed class DiscordRuntimeAdapter : INewfiesDiscordMotdProvider
    {
        private object? Provider
        {
            get
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("NewfiesServerEssentials.Discord.DiscordIntegrationModSystem", false))
                    .FirstOrDefault(t => t != null);
                return type?.GetProperty("CurrentMotdProvider", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
        }

        public int ProtocolVersion => NewfiesMotdIntegration.ProtocolVersion;
        public bool IsAvailable => ReadBool("IsAvailable");
        public event Action? ContentChanged { add { } remove { } }
        public IReadOnlyList<MotdFeedItem> GetAnnouncements() => ReadFeed("GetAnnouncements");
        public IReadOnlyList<MotdFeedItem> GetUpdates() => ReadFeed("GetUpdates");
        public IReadOnlyList<MotdFeedItem> GetEvents() => ReadFeed("GetEvents");

        private bool ReadBool(string property)
        {
            try { return Provider?.GetType().GetProperty(property)?.GetValue(Provider) as bool? == true; }
            catch { return false; }
        }

        private IReadOnlyList<MotdFeedItem> ReadFeed(string method)
        {
            try
            {
                var provider = Provider;
                var raw = provider?.GetType().GetMethod(method, Type.EmptyTypes)?.Invoke(provider, null) as IEnumerable;
                if (raw == null) return Array.Empty<MotdFeedItem>();
                var result = new List<MotdFeedItem>();
                foreach (var item in raw)
                {
                    if (item == null) continue;
                    var t = item.GetType();
                    object? Get(string n) => t.GetProperty(n)?.GetValue(item);
                    result.Add(new MotdFeedItem {
                        Id = Get("Id")?.ToString() ?? "",
                        Title = Get("Title")?.ToString() ?? "",
                        BodyVtml = Get("BodyVtml")?.ToString() ?? "",
                        Author = Get("Author")?.ToString() ?? "",
                        UnixTime = Convert.ToInt64(Get("UnixTime") ?? 0L),
                        Url = Get("Url")?.ToString() ?? "",
                        Important = Convert.ToBoolean(Get("Important") ?? false)
                    });
                }
                return result;
            }
            catch { return Array.Empty<MotdFeedItem>(); }
        }
    }
}
