using System.Net;
using System.Security.Cryptography;

namespace Newfies.WebsiteConnector;

public sealed class ConnectorConfig
{
    public bool Enabled { get; set; } = true;
    public bool AccountLinkingEnabled { get; set; } = true;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9466;
    public string AccessToken { get; set; } = "";
    public string SuitePublicOrigin { get; set; } = "http://127.0.0.1:9465";
    public int SnapshotCacheSeconds { get; set; } = 15;
    public int MaxConcurrentRequests { get; set; } = 16;
    public int UpstreamTimeoutSeconds { get; set; } = 8;
    public List<Supporter> Supporters { get; set; } = [];

    public void Normalize()
    {
        if (!IPAddress.TryParse(BindAddress, out _)) throw new ArgumentException("BindAddress must be an IP address.");
        if (Port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
        if (!Uri.TryCreate(SuitePublicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || !origin.IsLoopback || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("SuitePublicOrigin must be a loopback HTTP(S) origin for the Suite public map listener.");
        if (origin.Port == Port) throw new ArgumentException("Connector port must differ from the Suite public port.");
        SuitePublicOrigin = origin.GetLeftPart(UriPartial.Authority);
        if (string.IsNullOrEmpty(AccessToken)) AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        if (AccessToken.Length is < 32 or > 256 || AccessToken.Any(c => c <= ' ' || c >= 127))
            throw new ArgumentException("AccessToken must contain 32–256 printable characters without spaces.");
        SnapshotCacheSeconds = Math.Clamp(SnapshotCacheSeconds, 5, 300);
        MaxConcurrentRequests = Math.Clamp(MaxConcurrentRequests, 1, 32);
        UpstreamTimeoutSeconds = Math.Clamp(UpstreamTimeoutSeconds, 2, 15);
        Supporters ??= [];
        if (Supporters.Count > 1000) throw new ArgumentException("At most 1000 supporters are supported.");
        foreach (var s in Supporters)
        {
            if (s == null) throw new ArgumentException("Supporter entries must be objects.");
            s.Name = Clean(s.Name, 60); s.Tier = Clean(s.Tier, 40); s.Since = Clean(s.Since, 40);
            if (s.Name.Length == 0 || s.Tier.Length == 0) throw new ArgumentException("Supporters require a name and tier.");
        }
    }
    private static string Clean(string? value, int max)
    {
        var s = (value ?? "").Trim();
        if (s.Length > max || s.Any(char.IsControl)) throw new ArgumentException("Supporter text exceeds its limit or contains control characters.");
        return s;
    }
}
public sealed class Supporter
{
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "Supporter";
    public string Since { get; set; } = "";
}
