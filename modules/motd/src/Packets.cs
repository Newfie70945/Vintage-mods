using System;
using System.Collections.Generic;
using ProtoBuf;

namespace NewfiesCommunityMOTD
{
    [ProtoContract(SkipConstructor = true)]
    [Serializable]
    public class MotdPlayerEntry
    {
        [ProtoMember(1)] public string Name { get; set; } = "";
        [ProtoMember(2)] public string Role { get; set; } = "";
        [ProtoMember(3)] public int Ping { get; set; }
        [ProtoMember(4)] public bool IsStaff { get; set; }
    }

    [ProtoContract(SkipConstructor = true)]
    [Serializable]
    public class MotdPacket
    {
        [ProtoMember(1)] public string Title { get; set; } = "";
        [ProtoMember(2)] public string ContentVtml { get; set; } = "";
        [ProtoMember(3)] public int Width { get; set; }
        [ProtoMember(4)] public int Height { get; set; }
        [ProtoMember(5)] public int ScrollBarWidth { get; set; }
        [ProtoMember(6)] public float ScrollEstimateMultiplier { get; set; }
        [ProtoMember(7)] public int ScrollExtraPixels { get; set; }
        [ProtoMember(8)] public int ScrollOverrideHeightPx { get; set; }
        [ProtoMember(9)] public bool OpenOnEveryJoin { get; set; }
        [ProtoMember(10)] public bool OnlyOncePerSession { get; set; }
        [ProtoMember(11)] public bool OnlyOncePerVersion { get; set; }
        [ProtoMember(12)] public string VersionLabel { get; set; } = "";
        [ProtoMember(13)] public string ContentHash { get; set; } = "";
        [ProtoMember(14)] public bool ForceShow { get; set; }
        [ProtoMember(15)] public bool EnableDontShowAgain { get; set; } = true;
        [ProtoMember(16)] public bool EnableLinkButton { get; set; }
        [ProtoMember(17)] public string LinkButtonText { get; set; } = "";
        [ProtoMember(18)] public string LinkUrl { get; set; } = "";
        [ProtoMember(19)] public string Subtitle { get; set; } = "";
        [ProtoMember(20)] public string AccentColor { get; set; } = "#FFC857";
        [ProtoMember(21)] public bool EnableLogo { get; set; }
        [ProtoMember(22)] public string LogoAsset { get; set; } = "";
        [ProtoMember(23)] public int LogoWidth { get; set; }
        [ProtoMember(24)] public int LogoHeight { get; set; }
        [ProtoMember(25)] public bool EnablePlayersTab { get; set; }
        [ProtoMember(26)] public bool ShowPlayerRoles { get; set; }
        [ProtoMember(27)] public bool ShowPlayerPing { get; set; }
        [ProtoMember(28)] public bool SeparateStaff { get; set; }
        [ProtoMember(29)] public int OnlineCount { get; set; }
        [ProtoMember(30)] public int MaxPlayers { get; set; }
        [ProtoMember(31)] public List<MotdPlayerEntry> Players { get; set; } = new();
        [ProtoMember(32)] public string FooterText { get; set; } = "";
        [ProtoMember(33)] public bool EnableAnnouncementsTab { get; set; }
        [ProtoMember(34)] public bool EnableUpdatesTab { get; set; }
        [ProtoMember(35)] public bool EnableEventsTab { get; set; }
        [ProtoMember(36)] public bool EnableRulesTab { get; set; }
        [ProtoMember(37)] public bool EnableServerTab { get; set; }
        [ProtoMember(38)] public bool EnableCommandsTab { get; set; }
        [ProtoMember(39)] public bool EnableCommunityTab { get; set; }
        [ProtoMember(40)] public string AnnouncementsVtml { get; set; } = "";
        [ProtoMember(41)] public string UpdatesVtml { get; set; } = "";
        [ProtoMember(42)] public string EventsVtml { get; set; } = "";
        [ProtoMember(43)] public string RulesVtml { get; set; } = "";
        [ProtoMember(44)] public string ServerVtml { get; set; } = "";
        [ProtoMember(45)] public string CommandsVtml { get; set; } = "";
        [ProtoMember(46)] public string CommunityVtml { get; set; } = "";
        [ProtoMember(47)] public bool DiscordConnected { get; set; }
        [ProtoMember(48)] public bool SuiteConnected { get; set; }
        [ProtoMember(49)] public int IntegrationProtocol { get; set; } = 1;
        [ProtoMember(50)] public bool EnableWebMapButton { get; set; }
        [ProtoMember(51)] public string WebMapButtonText { get; set; } = "Web Map";
        [ProtoMember(52)] public string WebMapUrl { get; set; } = "";
        [ProtoMember(53)] public string InGameDate { get; set; } = "";
        [ProtoMember(54)] public string ServerInfoLeftVtml { get; set; } = "";
        [ProtoMember(55)] public string ServerInfoRightVtml { get; set; } = "";
        [ProtoMember(56)] public string InGameTime { get; set; } = "";
        [ProtoMember(57)] public string Season { get; set; } = "";
        [ProtoMember(58)] public bool IsRefresh { get; set; }
        [ProtoMember(59)] public List<string> AnnouncementIds { get; set; } = new();
        [ProtoMember(60)] public List<string> UpdateIds { get; set; } = new();
        [ProtoMember(61)] public string LogoSource { get; set; } = "Asset";
        [ProtoMember(62)] public string ServerLogoHash { get; set; } = "";
        [ProtoMember(63)] public byte[] ServerLogoPng { get; set; } = Array.Empty<byte>();
        [ProtoMember(64)] public bool LogoPreserveAspectRatio { get; set; } = true;
    }

    [ProtoContract(SkipConstructor = true)]
    [Serializable]
    public class MotdResetPacket
    {
        [ProtoMember(1)] public bool ResetAll { get; set; }
    }
}
