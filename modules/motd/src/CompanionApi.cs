using System;
using System.Collections.Generic;

namespace NewfiesCommunityMOTD
{
    public sealed class MotdFeedItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string BodyVtml { get; set; } = "";
        public string Author { get; set; } = "";
        public long UnixTime { get; set; }
        public string Url { get; set; } = "";
        public bool Important { get; set; }
    }

    public interface INewfiesDiscordMotdProvider
    {
        int ProtocolVersion { get; }
        bool IsAvailable { get; }
        IReadOnlyList<MotdFeedItem> GetAnnouncements();
        IReadOnlyList<MotdFeedItem> GetUpdates();
        IReadOnlyList<MotdFeedItem> GetEvents();
        event Action? ContentChanged;
    }

    public interface INewfiesSuiteMotdProvider
    {
        int ProtocolVersion { get; }
        bool IsAvailable { get; }
        string GetPlayerFacingStatusVtml();
        event Action? StatusChanged;
    }

    // Stable companion boundary. Discord/Suite may reference this assembly and register a provider.
    public static class NewfiesMotdIntegration
    {
        public const int ProtocolVersion = 1;
        public static INewfiesDiscordMotdProvider? Discord { get; private set; }
        public static INewfiesSuiteMotdProvider? Suite { get; private set; }
        public static event Action? ProvidersChanged;

        public static bool RegisterDiscord(INewfiesDiscordMotdProvider provider)
        {
            if (provider == null || provider.ProtocolVersion != ProtocolVersion) return false;
            Discord = provider; ProvidersChanged?.Invoke(); return true;
        }
        public static bool RegisterSuite(INewfiesSuiteMotdProvider provider)
        {
            if (provider == null || provider.ProtocolVersion != ProtocolVersion) return false;
            Suite = provider; ProvidersChanged?.Invoke(); return true;
        }
        public static void UnregisterDiscord(INewfiesDiscordMotdProvider provider) { if (ReferenceEquals(Discord, provider)) { Discord = null; ProvidersChanged?.Invoke(); } }
        public static void UnregisterSuite(INewfiesSuiteMotdProvider provider) { if (ReferenceEquals(Suite, provider)) { Suite = null; ProvidersChanged?.Invoke(); } }
    }
}
