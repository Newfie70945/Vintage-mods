using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BlockLogger.PlayerCommands;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace BlockLogger.Roles;

public sealed class RoleBatchRequest
{
    public string Revision { get; set; } = "";
    public string RoleCode { get; set; } = "";
    public bool Preview { get; set; } = true;
    public List<RolePlayerRequest> Players { get; set; } = new();
}

public sealed partial class RoleManagementService
{
    // Deterministic calculation shared by saved-player and unsaved-definition previews.
    public static object DescribePolicy(RoleEditorSnapshot snapshot, RoleEditorRow row,
        IEnumerable<string> grants, IEnumerable<string> denials, int? playerHomeLimit = null,
        int extraClaimAllowance = 0, int extraClaimAreas = 0, int? namedRoleHomeLimit = null)
    {
        var denied = new HashSet<string>(denials, StringComparer.OrdinalIgnoreCase);
        var privileges = (row.AutoGrant ? snapshot.KnownPrivileges : row.Privileges)
            .Concat(row.RuntimePrivileges).Concat(grants).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !denied.Contains(p)).OrderBy(p => p).ToArray();
        bool administrative = privileges.Any(p => p is "root" or "controlserver" or "*");
        var commands = Commands.Select(command => {
            row.Policies.TryGetValue(command, out var explicitPolicy);
            snapshot.WildcardPolicies.TryGetValue(command, out var wildcard);
            var selected = explicitPolicy ?? wildcard;
            string source = explicitPolicy != null ? "Role override" : wildcard != null ? "Wildcard override" : "Command default";
            bool allowed = selected?.Allowed ?? (command is not ("t2p" or "t2m") || administrative);
            int cooldown = selected?.CooldownSeconds >= 0 ? selected.CooldownSeconds : snapshot.DefaultCooldowns.GetValueOrDefault(command);
            return new { command, allowed, cooldownSeconds = cooldown, source };
        }).ToArray();
        return new { row.Code, row.Name, privileges, deniedPrivileges = denied.OrderBy(x => x).ToArray(),
            homeLimit = playerHomeLimit ?? row.HomeLimit ?? namedRoleHomeLimit ?? snapshot.GlobalHomeLimit,
            homeSource = playerHomeLimit != null ? "Player override" : row.HomeLimit != null || namedRoleHomeLimit != null ? "Role override" : "Global default",
            commands, claimAllowance = (int)Math.Clamp((long)row.LandClaimAllowance + extraClaimAllowance, 0, int.MaxValue),
            claimAreas = (int)Math.Clamp((long)row.LandClaimMaxAreas + extraClaimAreas, 0, int.MaxValue),
            note = "Preview preserves direct grants/denials. Runtime grants can change when another mod changes them; teleport warmups and command feature gates remain separate." };
    }

    public object PreviewPlayer(string uid, string targetRole) => PreviewPlayer(uid, targetRole, GetSnapshot());

    private object PreviewPlayer(string uid, string targetRole, RoleEditorSnapshot snapshot)
    {
        var data = api.PlayerData.GetPlayerDataByUid(uid);
        if (data == null) return new { available = false, message = "Player not found." };
        var row = snapshot.Roles.FirstOrDefault(r => string.Equals(r.Code, string.IsNullOrWhiteSpace(targetRole) ? data.RoleCode : targetRole, StringComparison.OrdinalIgnoreCase));
        if (row == null) return new { available = false, message = "Role not found." };
        var config = policyConfig(); int? home = null, namedHome = null;
        if (config != null)
        {
            if (config.HomePlayerLimits.TryGetValue(uid, out int playerLimit) || config.HomePlayerLimits.TryGetValue(data.LastKnownPlayername, out playerLimit)) home = Math.Max(0, playerLimit);
            if (row.HomeLimit == null && config.HomeRoleLimits.TryGetValue(row.Name, out int namedLimit)) namedHome = Math.Max(0, namedLimit);
        }
        var stored = data as IServerPlayerData;
        return new { available = true, playerUid = uid, playerName = data.LastKnownPlayername, currentRole = data.RoleCode,
            revision = snapshot.Revision, effective = DescribePolicy(snapshot, row, api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerUID == uid && row.Code == data.RoleCode)?.Privileges ?? (IEnumerable<string>)(stored?.PermaPrivileges ?? new HashSet<string>()), stored?.DeniedPrivileges ?? new HashSet<string>(), home, stored?.ExtraLandClaimAllowance ?? 0, stored?.ExtraLandClaimAreas ?? 0, namedHome) };
    }

    public object PreviewDefinition(RoleEditorRequest? request)
    {
        var snapshot = GetSnapshot();
        if (request?.Role == null)
            return new { success = false, message = "Invalid role preview." };
        if (!snapshot.Available) return new { success = false, message = "Role definitions require the supported Vintage Story server configuration." };
        if (request.Revision != snapshot.Revision) return new { success = false, conflict = true, message = "Role definitions changed; reload before previewing." };
        var row = request.Role; row.Privileges ??= new(); row.Policies ??= new();
        string? error = ValidateDefinitionFields(row);
        if (error != null) return new { success = false, message = error };
        string code = (row.Code ?? "").Trim().ToLowerInvariant();
        var existing = snapshot.Roles.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (request.Action is not ("create" or "save")) return new { success = false, message = "Preview a role creation or edit." };
        if (request.Action == "create")
        {
            if (existing != null) return new { success = false, message = "A role with this code already exists." };
            if (!Regex.IsMatch(code, "^[a-z0-9][a-z0-9_-]{0,63}$")) return new { success = false, message = "New role codes use 1–64 letters, numbers, underscores or hyphens." };
            if (snapshot.Roles.Count >= 1024) return new { success = false, message = "Role limit reached (1024)." };
        }
        else if (existing == null) return new { success = false, message = "The role no longer exists." };
        row.Privileges = row.Privileges.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Administrative(RoleEditorRow r) => r.AutoGrant || r.Privileges.Any(IsAdminPrivilege);
        if (existing != null && Administrative(existing) && !Administrative(row) && !snapshot.Roles.Any(r => r != existing && Administrative(r)))
            return new { success = false, message = "Keep at least one role with administrative privileges." };
        row.Code = code;

        row.RuntimePrivileges = snapshot.Roles.FirstOrDefault(r => r.Code == row.Code)?.RuntimePrivileges ?? new();
        return new { success = true, revision = snapshot.Revision, effective = DescribePolicy(snapshot, row, Array.Empty<string>(), Array.Empty<string>()),
            affectedPlayers = snapshot.Roles.FirstOrDefault(r => r.Code == row.Code)?.AssignedPlayers ?? 0 };
    }

    private static string? ValidateDefinitionFields(RoleEditorRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 80 || (row.Description?.Length ?? 0) > 500)
            return "Set a role name up to 80 characters and description up to 500.";
        if (!Regex.IsMatch(row.ColorHex ?? "", "^#[0-9a-fA-F]{6}$") || (!string.IsNullOrEmpty(row.NameColorHex) && !Regex.IsMatch(row.NameColorHex, "^#[0-9a-fA-F]{6}$"))) return "Colors must use #RRGGBB.";
        if (!Enum.TryParse<EnumGameMode>(row.DefaultGameMode, true, out var mode) || !Enum.IsDefined(mode)) return "Unknown default game mode.";
        if (row.PrivilegeLevel is < 0 or > 100000 || row.LandClaimAllowance < 0 || row.LandClaimMaxAreas is < 0 or > 10000 || row.ClaimMinX is < 1 or > 10000 || row.ClaimMinY is < 1 or > 10000 || row.ClaimMinZ is < 1 or > 10000) return "Role attributes are outside their supported bounds.";
        var privileges = (row.Privileges ?? new()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (privileges.Length > 1024 || privileges.Any(p => p.Length > 128 || p.Any(char.IsWhiteSpace))) return "Use privilege codes without whitespace (maximum 1024).";
        if (row.HomeLimit is < 0 or > 100) return "Home limit must be 0–100, or empty to inherit.";
        if ((row.Policies ?? new()).Any(p => !Commands.Contains(p.Key) || p.Value?.CooldownSeconds is < -1 or > 604800)) return "Invalid teleport policy or cooldown (−1 to inherit, 0–604800 otherwise).";
        return null;
    }

    public object ChangeBatch(RoleBatchRequest? request)
    {
        if (request?.Players == null || request.Players.Count is < 1 or > 50 || request.Players.Any(p => p == null || string.IsNullOrWhiteSpace(p.PlayerUid) || p.PlayerUid.Length > 128 || (p.ExpectedRoleCode?.Length ?? 0) > 64))
            return new { success = false, message = "Select 1–50 players." };
        var snapshot = GetSnapshot();
        if (request.Revision != snapshot.Revision) return new { success = false, conflict = true, message = "Role definitions changed. Preview again." };
        if (api.Permissions.GetRole(request.RoleCode) == null) return new { success = false, message = "Target role not found." };
        if (request.Players.Select(p => p.PlayerUid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Players.Count)
            return new { success = false, message = "Duplicate selected players." };
        foreach (var p in request.Players)
        {
            var data = api.PlayerData.GetPlayerDataByUid(p.PlayerUid);
            if (data == null || data.RoleCode != p.ExpectedRoleCode)
                return new { success = false, conflict = true, message = "A selected player's role changed. Search and preview again; nothing was assigned." };
        }
        if (request.Preview) return new { success = true, preview = true, revision = snapshot.Revision,
            players = request.Players.Select(p => PreviewPlayer(p.PlayerUid, request.RoleCode, snapshot)).ToArray() };
        var results = new List<object>(); int applied = 0;
        foreach (var p in request.Players)
        {
            RoleEditorResult result;
            try { result = SetPlayerRole(new RolePlayerRequest { PlayerUid = p.PlayerUid, ExpectedRoleCode = p.ExpectedRoleCode, RoleCode = request.RoleCode }); }
            catch { result = new RoleEditorResult { Message = "Assignment failed; earlier successful assignments remain applied. Refresh before retrying." }; }
            results.Add(new { p.PlayerUid, result.Success, result.Message });
            if (result.Success) applied++; else break;
        }
        return new { success = applied == request.Players.Count, preview = false, applied, requested = request.Players.Count, results,
            message = $"Assigned {applied} of {request.Players.Count} players. Direct grants and denials were retained." };
    }
}
