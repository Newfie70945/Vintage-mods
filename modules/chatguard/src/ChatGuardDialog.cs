using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;

namespace NewfiesChatGuard;

public sealed class ChatGuardDialog : GuiDialog
{
    private readonly ChatGuardGuiClient client;
    private JObject snapshot = new();
    private JObject lastTest = new();
    private string tab = "overview";
    private string target = "";
    private string duration = "30";
    private string reason = "";
    private string testTarget = "";
    private string testMessage = "";
    private string status = "";
    private string bannedTerm = "";

    public ChatGuardDialog(ICoreClientAPI capi, ChatGuardGuiClient client) : base(capi)
    {
        this.client = client;
    }

    public override string ToggleKeyCombinationCode => ChatGuardGuiClient.HotkeyCode;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public void Update(ChatGuardGuiResponse response)
    {
        try { snapshot = JObject.Parse(string.IsNullOrWhiteSpace(response.SnapshotJson) ? "{}" : response.SnapshotJson); }
        catch { snapshot = new JObject(); }
        if (!string.IsNullOrWhiteSpace(response.TestJson))
        {
            try { lastTest = JObject.Parse(response.TestJson); }
            catch { lastTest = new JObject(); }
            tab = "tester";
        }
        status = response.Message ?? "";
        if (IsOpened()) Compose();
    }

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    private void Compose()
    {
        const double width = 1030;
        const double height = 730;
        var content = ElementBounds.Fixed(0, 0, width, height);
        var bg = ElementBounds.Fill.WithFixedPadding(12);
        bg.BothSizing = ElementSizing.FitToChildren;
        bg.WithChildren(content);
        var bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        ClearComposers();
        var c = capi.Gui.CreateCompo("newfies-chatguard-admin", bounds)
            .AddShadedDialogBG(bg, true)
            .BeginChildElements(bg)
            .AddRichtext($"<strong>Newfie's ChatGuard {H(snapshot["version"]?.ToString())}</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(22, 24, 560, 28))
            .AddSmallButton("Refresh", () => client.Refresh(), ElementBounds.Fixed(832, 22, 80, 30))
            .AddSmallButton("Close", () => { TryClose(); return true; }, ElementBounds.Fixed(920, 22, 80, 30));

        AddTab(c, "Overview", "overview", 18);
        AddTab(c, "Moderation", "moderation", 148);
        AddTab(c, "Rule Tester", "tester", 278);
        AddTab(c, "Rules", "rules", 408);
        AddTab(c, "Banned Words", "banned", 538);
        AddTab(c, "Activity", "activity", 668);

        c.AddInset(ElementBounds.Fixed(18, 102, 982, 560));
        switch (tab)
        {
            case "moderation": ComposeModeration(c); break;
            case "tester": ComposeTester(c); break;
            case "rules": ComposeRules(c); break;
            case "banned": ComposeBannedWords(c); break;
            case "activity": ComposeActivity(c); break;
            default: ComposeOverview(c); break;
        }

        c.AddRichtext(H(status), CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, 680, 960, 34), "cg-status")
         .EndChildElements()
         .Compose();
        SingleComposer = c;
        if (tab == "rules") ApplyRuleSwitchValues();
        if (tab == "moderation")
        {
            SingleComposer.GetTextInput("cg-target")?.SetValue(target);
            SingleComposer.GetTextInput("cg-duration")?.SetValue(duration);
            SingleComposer.GetTextInput("cg-reason")?.SetValue(reason);
        }
        if (tab == "banned")
        {
            JObject cfg = snapshot["config"] as JObject ?? new JObject();
            JObject blocked = cfg["BlockedTerms"] as JObject ?? new JObject();
            SingleComposer.GetSwitch("cg-banned-enabled")?.SetValue(blocked.Value<bool?>("Enabled") ?? false);
            SingleComposer.GetTextInput("cg-banned-minutes")?.SetValue((blocked.Value<int?>("MuteMinutes") ?? 10).ToString());
            SingleComposer.GetTextInput("cg-banned-term")?.SetValue(bannedTerm);
        }
        if (tab == "tester")
        {
            SingleComposer.GetTextInput("cg-test-target")?.SetValue(testTarget);
            SingleComposer.GetTextInput("cg-test-message")?.SetValue(testMessage);
        }
    }

    private void AddTab(GuiComposer c, string label, string key, double x) =>
        c.AddSmallButton(label, () => { tab = key; Compose(); return true; }, ElementBounds.Fixed(x, 64, 118, 30));

    private void ComposeOverview(GuiComposer c)
    {
        bool enabled = snapshot.Value<bool?>("enabled") ?? false;
        string mode = snapshot.Value<string>("mode") ?? "—";
        int mutes = snapshot.Value<int?>("activeMutes") ?? 0;
        int warned = snapshot.Value<int?>("warnedPlayers") ?? 0;
        string posture = !enabled ? "Disabled" : mode.Equals("LogOnly", StringComparison.OrdinalIgnoreCase) ? "Observe only" : "Enforcing";
        string html = $"<strong>Protection posture</strong><br>{H(posture)} · Mode {H(mode)}<br><br>" +
                      $"Scanned: <strong>{N("scannedMessages")}</strong>  ·  Rule hits: <strong>{N("matchedRules")}</strong>  ·  Blocked: <strong>{N("blockedMessages")}</strong><br>" +
                      $"Warnings issued: <strong>{N("warningsIssued")}</strong>  ·  Mutes issued: <strong>{N("mutesIssued")}</strong>  ·  Kicks: <strong>{N("kicksIssued")}</strong><br><br>" +
                      $"Active mutes: <strong>{mutes}</strong>  ·  Warned players: <strong>{warned}</strong><br>" +
                      $"Suite detected: <strong>{((snapshot.Value<bool?>("suiteIntegrated") ?? false) ? "Yes" : "No")}</strong><br><br>" +
                      "Use Moderation for sanctions, Rule Tester to dry-run messages, Rules for quick rule toggles, and Activity for recent actions.";
        c.AddRichtext(html, CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 110, 930, 450));
    }

    private void ComposeModeration(GuiComposer c)
    {
        c.AddRichtext("<strong>Manual moderation</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 108, 500, 26))
         .AddRichtext("Player / UID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 150, 120, 24))
         .AddTextInput(ElementBounds.Fixed(164, 144, 300, 30), v => target = v ?? "", CairoFont.WhiteSmallText(), "cg-target")
         .AddRichtext("Minutes", CairoFont.WhiteSmallText(), ElementBounds.Fixed(486, 150, 70, 24))
         .AddTextInput(ElementBounds.Fixed(556, 144, 90, 30), v => duration = v ?? "30", CairoFont.WhiteSmallText(), "cg-duration")
         .AddRichtext("Reason", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 194, 120, 24))
         .AddTextInput(ElementBounds.Fixed(164, 188, 620, 30), v => reason = v ?? "", CairoFont.WhiteSmallText(), "cg-reason")
         .AddSmallButton("Warn", () => Act("warn", false), ElementBounds.Fixed(40, 242, 110, 32))
         .AddSmallButton("Mute", () => Act("mute", false), ElementBounds.Fixed(162, 242, 110, 32))
         .AddSmallButton("Permanent", () => Act("mute", true), ElementBounds.Fixed(284, 242, 120, 32))
         .AddSmallButton("Unmute", () => Act("unmute", false), ElementBounds.Fixed(416, 242, 110, 32))
         .AddSmallButton("Clear warnings", () => Act("clearwarnings", false), ElementBounds.Fixed(538, 242, 150, 32));

        string sanctions = BuildSanctionSummary();
        c.AddRichtext("<strong>Active sanctions</strong><br>" + sanctions, CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 308, 920, 280));
    }

    private bool Act(string action, bool permanent)
    {
        int minutes = int.TryParse(duration, out int parsed) ? Math.Clamp(parsed, 1, 43200) : 30;
        if (string.IsNullOrWhiteSpace(target)) { status = "Enter a player name or UID."; Compose(); return true; }
        client.Action(action, target.Trim(), minutes, permanent, reason.Trim());
        return true;
    }

    private void ComposeTester(GuiComposer c)
    {
        c.AddRichtext("<strong>Dry-run Rule Tester</strong><br>Runs the real ChatGuard rule checks without warning, muting, kicking, blocking, writing audit entries, or incrementing counters.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 108, 900, 54))
         .AddRichtext("Online player (optional)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 178, 170, 24))
         .AddTextInput(ElementBounds.Fixed(214, 172, 300, 30), v => testTarget = v ?? "", CairoFont.WhiteSmallText(), "cg-test-target")
         .AddRichtext("Message", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 220, 90, 24))
         .AddTextInput(ElementBounds.Fixed(132, 214, 650, 30), v => testMessage = v ?? "", CairoFont.WhiteSmallText(), "cg-test-message")
         .AddSmallButton("Test", () => { if (testMessage.Trim().Length == 0) { status = "Enter a message to test."; Compose(); } else client.Test(testTarget.Trim(), testMessage); return true; }, ElementBounds.Fixed(800, 214, 100, 30));

        string result = lastTest.Count == 0 ? "No dry-run result yet." : FormatTest(lastTest);
        c.AddRichtext("<strong>Result</strong><br>" + result, CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 276, 920, 320));
    }

    private void ComposeRules(GuiComposer c)
    {
        JObject cfg = snapshot["config"] as JObject ?? new JObject();
        bool enabled = cfg.Value<bool?>("Enabled") ?? true;
        string mode = cfg.Value<string>("Mode") ?? "Enforce";
        c.AddRichtext("<strong>Quick rule controls</strong><br>Changes are staged locally; press Save Rules to apply them server-side.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 108, 900, 50))
         .AddRichtext("ChatGuard enabled", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 174, 160, 24))
         .AddSwitch(v => { cfg["Enabled"] = v; }, ElementBounds.Fixed(206, 170, 42, 24), "cg-enabled")
         .AddRichtext("Mode", CairoFont.WhiteSmallText(), ElementBounds.Fixed(290, 174, 60, 24))
         .AddDropDown(new[] { "Enforce", "LogOnly" }, new[] { "Enforce", "Log only" }, mode.Equals("LogOnly", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
             (value, selected) => { if (selected) cfg["Mode"] = value; }, ElementBounds.Fixed(350, 168, 160, 30), CairoFont.WhiteSmallText(), "cg-mode");

        string[] names = { "Spam", "Repeat", "Caps", "Links", "Mentions", "Length", "BlockedTerms" };
        double y = 220;
        foreach (string name in names)
        {
            JObject rule = cfg[name] as JObject ?? new JObject();
            cfg[name] = rule;
            bool ruleEnabled = rule.Value<bool?>("Enabled") ?? false;
            string action = rule.Value<string>("Action") ?? "Warn";
            string key = "cg-rule-" + name.ToLowerInvariant();
            c.AddRichtext(H(name), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y + 4, 150, 24))
             .AddSwitch(v => { rule["Enabled"] = v; }, ElementBounds.Fixed(194, y, 42, 24), key)
             .AddRichtext($"Action: <strong>{H(action)}</strong> · Session hits: <strong>{RuleHits(name)}</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(270, y + 4, 500, 24));
            y += 44;
        }
        c.AddSmallButton("Save Rules", () => { client.Save(cfg.ToString(Formatting.None)); return true; }, ElementBounds.Fixed(40, 550, 130, 32));

    }

    private void ApplyRuleSwitchValues()
    {
        JObject cfg = snapshot["config"] as JObject ?? new JObject();
        SingleComposer?.GetSwitch("cg-enabled")?.SetValue(cfg.Value<bool?>("Enabled") ?? true);
        foreach (string name in new[] { "Spam", "Repeat", "Caps", "Links", "Mentions", "Length", "BlockedTerms" })
        {
            bool value = (cfg[name] as JObject)?.Value<bool?>("Enabled") ?? false;
            SingleComposer?.GetSwitch("cg-rule-" + name.ToLowerInvariant())?.SetValue(value);
        }
    }

    private void ComposeBannedWords(GuiComposer c)
    {
        JObject cfg = snapshot["config"] as JObject ?? new JObject();
        JObject blocked = cfg["BlockedTerms"] as JObject ?? new JObject();
        cfg["BlockedTerms"] = blocked;
        JArray terms = blocked["Terms"] as JArray ?? new JArray();
        blocked["Terms"] = terms;
        string action = blocked.Value<string>("Action") ?? "Warn";

        c.AddRichtext("<strong>Banned words &amp; phrases</strong><br>Manage the blocked-term list used by ChatGuard. Matching is case-insensitive and each entry may be a word or phrase.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 120, 900, 54))
         .AddRichtext("Enabled", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 184, 80, 24))
         .AddSwitch(v => blocked["Enabled"] = v, ElementBounds.Fixed(118, 180, 42, 24), "cg-banned-enabled")
         .AddRichtext("Action", CairoFont.WhiteSmallText(), ElementBounds.Fixed(200, 184, 60, 24))
         .AddDropDown(new[] { "Warn", "Block", "Mute", "Kick" }, new[] { "Warn", "Block", "Mute", "Kick" }, Math.Max(0, Array.IndexOf(new[] { "Warn", "Block", "Mute", "Kick" }, action)),
             (value, selected) => { if (selected) blocked["Action"] = value; }, ElementBounds.Fixed(260, 178, 130, 30), CairoFont.WhiteSmallText(), "cg-banned-action")
         .AddRichtext("Mute min", CairoFont.WhiteSmallText(), ElementBounds.Fixed(420, 184, 70, 24))
         .AddTextInput(ElementBounds.Fixed(492, 178, 80, 30), v => { if (int.TryParse(v, out int n)) blocked["MuteMinutes"] = Math.Clamp(n, 1, 10080); }, CairoFont.WhiteSmallText(), "cg-banned-minutes")
         .AddRichtext("Add word / phrase", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 230, 130, 24))
         .AddTextInput(ElementBounds.Fixed(174, 224, 520, 30), v => bannedTerm = v ?? "", CairoFont.WhiteSmallText(), "cg-banned-term")
         .AddSmallButton("Add", () => { AddBannedTerm(terms); return true; }, ElementBounds.Fixed(708, 224, 90, 30))
         .AddSmallButton("Remove", () => { RemoveBannedTerm(terms); return true; }, ElementBounds.Fixed(808, 224, 100, 30));

        string list = terms.Count == 0
            ? "<em>No banned words or phrases configured.</em>"
            : string.Join("<br>", terms.Take(24).Select((x, i) => $"{i + 1}. {H(x?.ToString())}")) + (terms.Count > 24 ? $"<br><br>…and {terms.Count - 24} more." : "");
        c.AddRichtext($"<strong>Configured entries ({terms.Count})</strong><br><br>{list}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 282, 900, 300))
         .AddSmallButton("Save Banned Words", () => { client.Save(cfg.ToString(Formatting.None)); return true; }, ElementBounds.Fixed(40, 606, 170, 32))
         .AddSmallButton("Clear All", () => { terms.Clear(); status = "Banned-word list cleared locally. Press Save Banned Words to apply."; Compose(); return true; }, ElementBounds.Fixed(222, 606, 110, 32));

    }

    private void AddBannedTerm(JArray terms)
    {
        string value = (bannedTerm ?? "").Trim();
        if (value.Length == 0) { status = "Enter a word or phrase to add."; Compose(); return; }
        if (value.Length > 120) value = value[..120];
        if (terms.Any(x => string.Equals(x?.ToString(), value, StringComparison.OrdinalIgnoreCase))) { status = "That banned word or phrase is already configured."; Compose(); return; }
        if (terms.Count >= 500) { status = "The banned-word list is limited to 500 entries."; Compose(); return; }
        terms.Add(value); bannedTerm = ""; status = $"Added '{value}' locally. Press Save Banned Words to apply."; Compose();
    }

    private void RemoveBannedTerm(JArray terms)
    {
        string value = (bannedTerm ?? "").Trim();
        JToken? found = terms.FirstOrDefault(x => string.Equals(x?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (found == null) { status = "Enter an existing banned word or phrase to remove."; Compose(); return; }
        found.Remove(); bannedTerm = ""; status = $"Removed '{value}' locally. Press Save Banned Words to apply."; Compose();
    }

    private void ComposeActivity(GuiComposer c)
    {
        var recent = snapshot["recent"] as JArray ?? new JArray();
        var lines = recent.Take(18).Select(x =>
        {
            long ms = x.Value<long?>("TimestampUtcMs") ?? 0;
            string time = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss") : "—";
            return $"{H(time)}  <strong>{H(x.Value<string>("PlayerName") ?? "—")}</strong>  {H(x.Value<string>("Rule") ?? "—")}/{H(x.Value<string>("Action") ?? "—")}: {H(x.Value<string>("Detail") ?? "")}";
        });
        string html = recent.Count == 0 ? "No moderation activity recorded this session." : string.Join("<br>", lines);
        c.AddRichtext("<strong>Recent moderation activity</strong><br><br>" + html, CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 108, 920, 480));
    }

    private string BuildSanctionSummary()
    {
        var mutes = snapshot["mutes"] as JArray ?? new JArray();
        var warnings = snapshot["warnings"] as JArray ?? new JArray();
        var lines = new List<string>();
        foreach (var x in mutes.Take(7))
            lines.Add($"Mute · <strong>{H(x.Value<string>("PlayerName") ?? "—")}</strong> · {(x.Value<bool?>("Permanent") == true ? "permanent" : "temporary")} · {H(x.Value<string>("Reason") ?? "")}");
        foreach (var x in warnings.Take(7))
            lines.Add($"Warnings · <strong>{H(x.Value<string>("PlayerName") ?? "—")}</strong> · {x.Value<int?>("ActiveWarnings") ?? 0} active");
        return lines.Count == 0 ? "No active ChatGuard sanctions." : string.Join("<br>", lines);
    }

    private static string FormatTest(JObject test)
    {
        string summary = test.Value<bool?>("Matched") == true
            ? $"Would match <strong>{H(test.Value<string>("Rule") ?? "—")}</strong> → <strong>{H(test.Value<string>("Action") ?? "—")}</strong><br>{H(test.Value<string>("Detail") ?? "")}"
            : $"<strong>No enforced match.</strong><br>{H(test.Value<string>("Detail") ?? "")}";
        var checks = test["Checks"] as JArray ?? new JArray();
        string details = string.Join("<br>", checks.Select(x => $"{(x.Value<bool?>("Matched") == true ? "MATCH" : "pass")} · {H(x.Value<string>("Rule") ?? "—")}: {H(x.Value<string>("Detail") ?? "")}"));
        return summary + (details.Length > 0 ? "<br><br>" + details : "");
    }

    private long RuleHits(string configName)
    {
        string rule = configName switch { "Links" => "Link", "Mentions" => "MentionSpam", "Length" => "MessageLength", "BlockedTerms" => "BlockedTerm", _ => configName };
        var stats = snapshot["ruleStats"] as JArray ?? new JArray();
        return stats.FirstOrDefault(x => string.Equals(x.Value<string>("Rule"), rule, StringComparison.OrdinalIgnoreCase))?.Value<long?>("Matches") ?? 0;
    }

    private long N(string key) => snapshot.Value<long?>(key) ?? 0;
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        ClearComposers();
    }
}
