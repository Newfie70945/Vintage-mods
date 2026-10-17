using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlockLogger.PlayerCommands;
using BlockLogger.PlayerList;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace BlockLogger.Roles;

public sealed class RoleEditorRow
{
    public string Code { get; set; }="";
    public string Name { get; set; }="";
    public string Description { get; set; }="";
    public int PrivilegeLevel { get; set; }
    public string ColorHex { get; set; }="#FFFFFF";
    public string NameColorHex { get; set; }="";
    public bool AutoGrant { get; set; }
    public string DefaultGameMode { get; set; }="Survival";
    public int LandClaimAllowance { get; set; }
    public int LandClaimMaxAreas { get; set; }
    public int ClaimMinX { get; set; }=5;
    public int ClaimMinY { get; set; }=5;
    public int ClaimMinZ { get; set; }=5;
    public List<string> Privileges { get; set; }=new();
    public List<string> RuntimePrivileges { get; set; }=new();
    public int? HomeLimit { get; set; }
    public Dictionary<string,TeleportRolePolicy?> Policies { get; set; }=new();
    public int AssignedPlayers { get; set; }
    public bool IsDefault { get; set; }
}
public sealed class RoleEditorSnapshot
{
    public bool Available { get; set; }
    public bool PoliciesAvailable { get; set; }
    public bool ColorsAvailable { get; set; }
    public bool UseRoleNameColors { get; set; }
    public string Revision { get; set; }="";
    public int GlobalHomeLimit { get; set; }
    public List<string> KnownPrivileges { get; set; }=new();
    public List<string> GameModes { get; set; }=new();
    public Dictionary<string,int> DefaultCooldowns { get; set; }=new();
    public Dictionary<string,TeleportRolePolicy> WildcardPolicies {get;set;}=new();
    public List<RoleEditorRow> Roles { get; set; }=new();
}
public sealed class RoleEditorRequest
{
    public string Action { get; set; }="save";
    public string Revision { get; set; }="";
    public RoleEditorRow Role { get; set; }=new();
    public bool? UseRoleNameColors { get; set; }
}
public sealed class RolePlayerRequest
{
    public string PlayerUid { get; set; }="";
    public string RoleCode { get; set; }="";
    public string ExpectedRoleCode { get; set; }="";
}
public sealed class RoleEditorResult
{
    public bool Success { get; set; }
    public bool Conflict { get; set; }
    public string Message { get; set; }="";
    public RoleEditorSnapshot? Snapshot { get; set; }
}

/// <summary>Called on the game thread. Role objects stay in place so engine references remain valid.</summary>
public sealed partial class RoleManagementService
{
    private readonly ICoreServerAPI api;
    private readonly Func<PlayerCommandConfig?> policyConfig;
    private readonly Func<PublicPlayerListServer?> playerList;
    public static readonly string[] Commands={"spawn","home","back","warp","t2p","t2m","rtp"};
    public RoleManagementService(ICoreServerAPI api,Func<PlayerCommandConfig?> policyConfig,Func<PublicPlayerListServer?> playerList)
    {this.api=api;this.policyConfig=policyConfig;this.playerList=playerList;}

    public RoleEditorSnapshot GetSnapshot()
    {
        var config=api.Server.Config;
        var policies=policyConfig();var colors=playerList()?.Config;
        var counts=(api.PlayerData.PlayerDataByUid??new()).Values.GroupBy(x=>x.RoleCode??"",StringComparer.OrdinalIgnoreCase).ToDictionary(x=>x.Key,x=>x.Count(),StringComparer.OrdinalIgnoreCase);
        var snapshot=new RoleEditorSnapshot {Available=config is ServerConfig,PoliciesAvailable=policies!=null,ColorsAvailable=colors!=null,UseRoleNameColors=colors?.UseRoleNameColors??false,GlobalHomeLimit=policies?.HomeGlobalLimit??0,KnownPrivileges=Privilege.AllCodes().OrderBy(x=>x).ToList(),GameModes=Enum.GetNames<EnumGameMode>().ToList()};
        if(policies!=null)snapshot.DefaultCooldowns=new(){{"spawn",policies.SpawnCooldownSeconds},{"home",policies.HomeCooldownSeconds},{"back",policies.BackCooldownSeconds},{"warp",policies.WarpDefaultCooldownSeconds},{"t2p",policies.T2pCooldownSeconds},{"t2m",policies.T2mCooldownSeconds},{"rtp",policies.RtpCooldownSeconds}};
        foreach(var cmd in Commands)if(policies?.TeleportRolePolicies.GetValueOrDefault(cmd)?.GetValueOrDefault("*") is {} wildcard)snapshot.WildcardPolicies[cmd]=new(){Allowed=wildcard.Allowed,CooldownSeconds=wildcard.CooldownSeconds};
        foreach(var role in config.Roles.OrderBy(x=>x.Code,StringComparer.OrdinalIgnoreCase))
        {
            var row=new RoleEditorRow {Code=role.Code,Name=role.Name,Description=role.Description??"",PrivilegeLevel=role.PrivilegeLevel,ColorHex=$"#{role.Color.R:X2}{role.Color.G:X2}{role.Color.B:X2}",AutoGrant=role.AutoGrant,DefaultGameMode=role.DefaultGameMode.ToString(),LandClaimAllowance=role.LandClaimAllowance,LandClaimMaxAreas=role.LandClaimMaxAreas,ClaimMinX=role.LandClaimMinSize?.X??5,ClaimMinY=role.LandClaimMinSize?.Y??5,ClaimMinZ=role.LandClaimMinSize?.Z??5,Privileges=(role.Privileges??new()).OrderBy(x=>x).ToList(),RuntimePrivileges=(role.RuntimePrivileges??new()).OrderBy(x=>x).ToList(),IsDefault=string.Equals(config.DefaultRoleCode,role.Code,StringComparison.OrdinalIgnoreCase),AssignedPlayers=counts.GetValueOrDefault(role.Code)};
            if(colors?.RoleNameColors.TryGetValue(role.Code,out var color)==true)row.NameColorHex=color;
            if(policies?.HomeRoleLimits.TryGetValue(role.Code,out var limit)==true)row.HomeLimit=limit;
            foreach(var cmd in Commands){var map=policies?.TeleportRolePolicies.GetValueOrDefault(cmd);row.Policies[cmd]=map?.TryGetValue(role.Code,out var p)==true?new TeleportRolePolicy {Allowed=p.Allowed,CooldownSeconds=p.CooldownSeconds}:null;}
            snapshot.Roles.Add(row);
        }
        // Player membership changes do not conflict with a definition edit.
        snapshot.Revision=Hash(JsonSerializer.Serialize(new {roles=snapshot.Roles.Select(r=>new {r.Code,r.Name,r.Description,r.PrivilegeLevel,r.ColorHex,r.NameColorHex,r.AutoGrant,r.DefaultGameMode,r.LandClaimAllowance,r.LandClaimMaxAreas,r.ClaimMinX,r.ClaimMinY,r.ClaimMinZ,r.Privileges,r.RuntimePrivileges,r.HomeLimit,r.Policies}),snapshot.UseRoleNameColors,policyRevision=policies==null?"":PolicyRevision(policies)}));
        return snapshot;
    }
    public static string PolicyRevision(PlayerCommandConfig config)=>Hash(JsonSerializer.Serialize(new {
        config.HomeGlobalLimit,limits=config.HomeRoleLimits.OrderBy(x=>x.Key),
        policies=config.TeleportRolePolicies.OrderBy(x=>x.Key).Select(x=>new {command=x.Key,roles=x.Value.OrderBy(v=>v.Key)}),
        config.SpawnCooldownSeconds,config.HomeCooldownSeconds,config.BackCooldownSeconds,config.WarpDefaultCooldownSeconds,config.T2pCooldownSeconds,config.T2mCooldownSeconds,config.RtpCooldownSeconds}));
    public static string ColorRevision(PublicPlayerListConfig? config)=>Hash(JsonSerializer.Serialize(new {enabled=config?.UseRoleNameColors??false,colors=config?.RoleNameColors.OrderBy(x=>x.Key)}));
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public RoleEditorResult Change(RoleEditorRequest? request)
    {
        var snapshot=GetSnapshot();
        RoleEditorResult Fail(string message,bool conflict=false)=>new(){Message=message,Conflict=conflict};
        if(!snapshot.Available||api.Server.Config is not ServerConfig config)return Fail("Role definitions require the supported Vintage Story server configuration.");
        if(request==null||request.Role==null)return Fail("Role request is required.");
        if(request.Revision!=snapshot.Revision)return Fail("Roles or shared policies changed elsewhere. Reload before saving; your draft has been kept.",true);
        var incoming=request.Role;string code=(incoming.Code??"").Trim().ToLowerInvariant();
        var existing=config.Roles.FirstOrDefault(r=>string.Equals(r.Code,code,StringComparison.OrdinalIgnoreCase));
        bool create=request.Action=="create",delete=request.Action=="delete";
        if(request.Action is not ("create" or "save" or "delete"))return Fail("Unknown role action.");
        if(create&&existing!=null)return Fail("A role with this code already exists.");
        if(!create&&existing==null)return Fail("The role no longer exists.");
        if(create&&!Regex.IsMatch(code,"^[a-z0-9][a-z0-9_-]{0,63}$"))return Fail("New role codes use 1–64 letters, numbers, underscores or hyphens.");
        if(create&&config.Roles.Count>=1024)return Fail("Role limit reached (1024).");
        if(delete)
        {
            if(string.Equals(config.DefaultRoleCode,code,StringComparison.OrdinalIgnoreCase))return Fail("The server default role cannot be removed.");
            if(snapshot.Roles.First(r=>r.Code.Equals(code,StringComparison.OrdinalIgnoreCase)).AssignedPlayers>0)return Fail("Reassign this role's online and offline players before removing it.");
            if(IsAdmin(existing!)&&!config.Roles.Any(r=>r!=existing&&IsAdmin(r)))return Fail("Keep at least one administrative game role.");
        }
        else
        {
            string? fieldError = ValidateDefinitionFields(incoming);
            if (fieldError != null) return Fail(fieldError);
            if(string.IsNullOrWhiteSpace(incoming.Name)||incoming.Name.Length>80||(incoming.Description??"").Length>500)return Fail("Set a role name up to 80 characters and description up to 500.");
            if(!Regex.IsMatch(incoming.ColorHex??"","^#[0-9a-fA-F]{6}$")||(!string.IsNullOrEmpty(incoming.NameColorHex)&&!Regex.IsMatch(incoming.NameColorHex,"^#[0-9a-fA-F]{6}$")))return Fail("Colors must use #RRGGBB.");
            if(!Enum.TryParse<EnumGameMode>(incoming.DefaultGameMode,true,out var mode)||!Enum.IsDefined(mode))return Fail("Unknown default game mode.");
            if(incoming.PrivilegeLevel<0||incoming.PrivilegeLevel>100000||incoming.LandClaimAllowance<0||incoming.LandClaimMaxAreas<0||incoming.LandClaimMaxAreas>10000||incoming.ClaimMinX<1||incoming.ClaimMinY<1||incoming.ClaimMinZ<1||incoming.ClaimMinX>10000||incoming.ClaimMinY>10000||incoming.ClaimMinZ>10000)return Fail("Role attributes are outside their supported bounds.");
            incoming.Privileges=(incoming.Privileges??new()).Select(x=>(x??"").Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if(incoming.Privileges.Count>1024||incoming.Privileges.Any(x=>x.Length>128||x.Any(char.IsWhiteSpace)))return Fail("Use privilege codes without whitespace (maximum 1024).");
            if(incoming.AutoGrant)incoming.Privileges=incoming.Privileges.Union(Privilege.AllCodes(),StringComparer.OrdinalIgnoreCase).ToList();
            if(existing!=null&&IsAdmin(existing)&&!incoming.AutoGrant&&!incoming.Privileges.Any(IsAdminPrivilege)&&!config.Roles.Any(r=>r!=existing&&IsAdmin(r)))return Fail("Keep at least one role with administrative privileges.");
            if(incoming.HomeLimit is <0 or >100)return Fail("Home limit must be 0–100, or empty to inherit.");
            incoming.Policies??=new();
            if(incoming.Policies.Any(x=>!Commands.Contains(x.Key)||x.Value?.CooldownSeconds is <-1 or >604800))return Fail("Invalid teleport policy or cooldown (−1 to inherit, 0–604800 otherwise).");
        }
        var policy=policyConfig();var list=playerList();
        var oldLimits=policy?.HomeRoleLimits;var oldPolicies=policy?.TeleportRolePolicies;
        var oldColors=list?.Config;
        try
        {
            if(policy!=null)
            {
                var limits=new Dictionary<string,int>(policy.HomeRoleLimits,StringComparer.OrdinalIgnoreCase);
                var maps=policy.TeleportRolePolicies.ToDictionary(x=>x.Key,x=>new Dictionary<string,TeleportRolePolicy>(x.Value,StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase);
                limits.Remove(code);if(!delete&&incoming.HomeLimit.HasValue)limits[code]=incoming.HomeLimit.Value;
                foreach(var cmd in Commands){maps.TryGetValue(cmd,out var map);map??=new(StringComparer.OrdinalIgnoreCase);map.Remove(code);if(!delete&&incoming.Policies.GetValueOrDefault(cmd) is { } p)map[code]=new(){Allowed=p.Allowed,CooldownSeconds=p.CooldownSeconds};if(map.Count>0)maps[cmd]=map;else maps.Remove(cmd);}
                policy.HomeRoleLimits=limits;policy.TeleportRolePolicies=maps;
                SuiteConfigStorage.Store(api,policy,PlayerCommandConfig.ConfigFile);
            }
            if(list!=null)
            {
                var map=new Dictionary<string,string>(list.Config.RoleNameColors,StringComparer.OrdinalIgnoreCase);map.Remove(code);
                if(!delete&&!string.IsNullOrEmpty(incoming.NameColorHex))map[code]=incoming.NameColorHex.ToUpperInvariant();
                list.ApplyRoleNameColors(request.UseRoleNameColors??list.Config.UseRoleNameColors,map);
            }
        }
        catch(Exception ex)
        {
            if(policy!=null){policy.HomeRoleLimits=oldLimits!;policy.TeleportRolePolicies=oldPolicies!;try{SuiteConfigStorage.Store(api,policy,PlayerCommandConfig.ConfigFile);}catch{}}
            if(list!=null&&oldColors!=null){try{list.ApplyConfig(oldColors);}catch{}}
            return Fail("Role settings could not be saved. Role definitions were left unchanged. "+ex.Message);
        }
        if(delete){config.Roles.Remove(existing!);config.RolesByCode.Remove(existing!.Code);}
        else
        {
            var role=existing??new PlayerRole {Code=code};
            role.Name=incoming.Name.Trim();role.Description=(incoming.Description??"").Trim();role.PrivilegeLevel=incoming.PrivilegeLevel;role.Color=Color.FromArgb(Convert.ToInt32(incoming.ColorHex![1..3],16),Convert.ToInt32(incoming.ColorHex![3..5],16),Convert.ToInt32(incoming.ColorHex![5..7],16));role.AutoGrant=incoming.AutoGrant;role.DefaultGameMode=Enum.Parse<EnumGameMode>(incoming.DefaultGameMode,true);role.LandClaimAllowance=incoming.LandClaimAllowance;role.LandClaimMaxAreas=incoming.LandClaimMaxAreas;role.LandClaimMinSize=new Vec3i(incoming.ClaimMinX,incoming.ClaimMinY,incoming.ClaimMinZ);role.Privileges=incoming.Privileges;
            if(create){config.Roles.Add(role);config.RolesByCode[code]=role;}
        }
        api.Server.MarkConfigDirty();
        try{(api.World as ServerMain)?.BroadcastRoles();foreach(var p in api.World.AllOnlinePlayers.OfType<IServerPlayer>())if(p.Role?.Code.Equals(code,StringComparison.OrdinalIgnoreCase)==true)p.BroadcastPlayerData(false);}catch(Exception ex){api.Logger.Warning("[Suite/Roles] Saved role but client display refresh failed: {0}",ex.Message);}
        return new(){Success=true,Message=delete?"Role removed. Shared Suite role overrides were cleared.":"Role and shared policies applied. Vintage Story will persist serverconfig.json through its normal save loop.",Snapshot=GetSnapshot()};
    }
    private static bool IsAdminPrivilege(string code)=>code.Equals("controlserver",StringComparison.OrdinalIgnoreCase)||code.Equals("root",StringComparison.OrdinalIgnoreCase)||code=="*";
    private static bool IsAdmin(PlayerRole role)=>role.AutoGrant||(role.Privileges??new()).Any(IsAdminPrivilege);

    public object GetPlayers(string query,int limit)
    {
        query=(query??"").Trim();limit=Math.Clamp(limit,1,100);
        var online=new HashSet<string>(api.World.AllOnlinePlayers.Select(x=>x.PlayerUID),StringComparer.OrdinalIgnoreCase);
        var matches=api.PlayerData.PlayerDataByUid.Values.Where(p=>query.Length==0||p.LastKnownPlayername.Contains(query,StringComparison.OrdinalIgnoreCase)||p.PlayerUID.Contains(query,StringComparison.OrdinalIgnoreCase)||p.RoleCode.Contains(query,StringComparison.OrdinalIgnoreCase)).OrderByDescending(p=>online.Contains(p.PlayerUID)).ThenBy(p=>p.LastKnownPlayername).ToList();
        return new {total=matches.Count,players=matches.Take(limit).Select(p=>new {playerUid=p.PlayerUID,playerName=p.LastKnownPlayername,roleCode=p.RoleCode,online=online.Contains(p.PlayerUID)})};
    }
    public RoleEditorResult SetPlayerRole(RolePlayerRequest? request)
    {
        if(request==null||string.IsNullOrWhiteSpace(request.PlayerUid)||string.IsNullOrWhiteSpace(request.RoleCode))return new(){Message="Choose a known player."};
        var role=api.Permissions.GetRole(request.RoleCode);
        var data=api.PlayerData.GetPlayerDataByUid(request.PlayerUid);
        if(role==null||data==null)return new(){Message="Player or role no longer exists."};
        if(data.RoleCode!=request.ExpectedRoleCode)return new(){Conflict=true,Message="This player's role changed elsewhere. Refresh the player list."};
        var online=api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(x=>x.PlayerUID==request.PlayerUid);
        if(online!=null)api.Permissions.SetRole(online,role);
        else if(data is ServerPlayerData stored&&api.PlayerData is PlayerDataManager manager){stored.RoleCode=role.Code;manager.playerDataDirty=true;}
        else return new(){Message="Offline role updates are unavailable in this server implementation."};
        return new(){Success=true,Message=$"Assigned {data.LastKnownPlayername} to {role.Name}. Existing direct grants/denials were retained."};
    }
}
