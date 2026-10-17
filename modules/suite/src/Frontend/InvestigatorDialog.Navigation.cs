using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

public sealed partial class InvestigatorDialog
{
    private string previousWorkspace = "home";
    private string lastWorkspace = "blocklogger";
    private string lastComposedWorkspace = "home";

    private void RememberWorkspaceNavigation()
    {
        if (section != lastComposedWorkspace) { previousWorkspace = lastComposedWorkspace; lastComposedWorkspace = section; }
        if (section != "home" && Array.IndexOf(SuiteSections, section) >= 0) lastWorkspace = section;
    }

    private static readonly string[] SuiteSections =
        { "home", "blocklogger", "animals", "profiler", "loginprotection", "webserver", "anticheat", "admincommands" };

    private static string SectionLabel(string value) => SuiteLocalization.Get(value switch
    {
        "blocklogger" => "gui.blocklogger",
        "animals" => "gui.animallimiter",
        "profiler" => "gui.profiler",
        "loginprotection" => "gui.loginprotection",
        "webserver" => "gui.integrations",
        "anticheat" => "gui.anticheat",
        "admincommands" => "gui.playeradmin",
        _ => "gui.workspace.home"
    });

    private static string SectionHelp(string value) => SuiteLocalization.Get(value switch
    {
        "blocklogger" => "gui.workspace.help.blocklogger",
        "animals" => "gui.workspace.help.animals",
        "profiler" => "gui.workspace.help.profiler",
        "loginprotection" => "gui.workspace.help.loginprotection",
        "webserver" => "gui.workspace.help.webserver",
        "anticheat" => "gui.workspace.help.anticheat",
        "admincommands" => "gui.workspace.help.admincommands",
        _ => "gui.workspace.homehelp"
    });

    private void BuildSuiteNavigation(GuiComposer composer, double width)
    {
        var sectionNames = Array.ConvertAll(SuiteSections, SectionLabel);
        composer
            .AddRichtext($"<strong>{SuiteLocalization.Get("gui.title")}</strong>", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(24, 12, width - 360, 26))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.back"), () => SelectSection(previousWorkspace), ElementBounds.Fixed(width - 288, 9, 78, 28))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.copyview"), () => { capi.Input.ClipboardText = $"{SectionLabel(section)}\n{pageHeaderText}\n{resultText}"; return true; }, ElementBounds.Fixed(width - 202, 9, 94, 28))
            .AddSuiteButton(SuiteLocalization.Get("gui.close"), () => { TryClose(); return true; }, ElementBounds.Fixed(width - 98, 9, 74, 28))
            .AddHoverText(SuiteLocalization.Get("gui.tip.close"), CairoFont.WhiteSmallText(), 280,
                ElementBounds.Fixed(width - 98, 9, 74, 28), "tip-close-investigator")
            .AddDropDown(SuiteSections, sectionNames, Math.Max(0, Array.IndexOf(SuiteSections, section)),
                (value, selected) => { if (selected) SelectSection(value); },
                ElementBounds.Fixed(24, 43, 258, 30), CairoFont.WhiteSmallText(), "suite-workspace-picker")
            .AddHoverText(SuiteLocalization.Get("gui.workspace.switchhelp"), CairoFont.WhiteSmallText(), 350,
                ElementBounds.Fixed(24, 43, 258, 30), "tip-suite-workspace-picker")
            .AddStaticText(SectionHelp(section), CairoFont.WhiteSmallText().WithColor(new[] { 0.67, 0.74, 0.82, 1.0 }),
                ElementBounds.Fixed(300, 44, width - 430, 30))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.home"), () => SelectSection("home"),
                ElementBounds.Fixed(width - 110, 43, 86, 30), section == "home");
    }

    private void BuildSuiteHome(GuiComposer composer)
    {
        composer
            .AddRichtext($"<font color=\"#7db6ff\"><strong>{SuiteLocalization.Get("gui.workspace.welcome")}</strong></font>",
                CairoFont.WhiteMediumText(), ElementBounds.Fixed(34, 132, 760, 34))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.resume", SectionLabel(lastWorkspace)), () => SelectSection(lastWorkspace), ElementBounds.Fixed(814, 128, 210, 34))
            .AddStaticText(SuiteLocalization.Get("gui.workspace.starthelp"), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(34, 171, 980, 44));

        // Stable two-column placement keeps all workspaces visible at once. Cards
        // describe purpose instead of displaying unrequested or stale server data.
        for (int i = 1; i < SuiteSections.Length; i++)
        {
            string target = SuiteSections[i];
            int slot = i - 1;
            double x = 28 + (slot % 2) * 506;
            double y = 228 + (slot / 2) * 108;
            composer
                .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(x, y, 494, 96)))
                .AddSuiteButton(SectionLabel(target), () => SelectSection(target), ElementBounds.Fixed(x + 16, y + 12, 226, 28))
                .AddStaticText(SectionHelp(target), CairoFont.WhiteSmallText().WithColor(new[] { 0.71, 0.77, 0.84, 1.0 }),
                    ElementBounds.Fixed(x + 16, y + 48, 462, 40));
        }

        composer
            .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(534, 552, 494, 96)))
            .AddStaticText(SuiteLocalization.Get("gui.workspace.shortcuts"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }),
                ElementBounds.Fixed(550, 563, 440, 24))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.investigate"), () => SelectAntiCheatView("players"),
                ElementBounds.Fixed(550, 601, 218, 28))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.audit"), () => SelectView("blocks"),
                ElementBounds.Fixed(778, 601, 232, 28))
            .AddStaticText(SuiteLocalization.Get("gui.workspace.permissions"), CairoFont.WhiteSmallText().WithColor(new[] { 0.64, 0.71, 0.78, 1.0 }),
                ElementBounds.Fixed(34, 684, 970, 38));
    }

    private bool SelectBlockSettingsView(string selected)
    {
        blockSettingsView = selected is "storage" or "queries" ? selected : "events";
        ComposeDialog();
        return true;
    }

    private void BuildBlockLoggerSettings(GuiComposer composer)
    {
        composer
            .AddRichtext($"<font color=\"#7db6ff\"><strong>{SuiteLocalization.Get("gui.blsettings2")}</strong></font>",
                CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, 132, 600, 26), "page-header")
            .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RequestBlockLoggerSettings, ElementBounds.Fixed(912, 128, 116, 30))
            .AddStaticText(SuiteLocalization.Get("gui.blsettingssave"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, 170, 980, 26))
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.events"), () => SelectBlockSettingsView("events"),
                ElementBounds.Fixed(34, 210, 168, 30), blockSettingsView == "events")
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.storage"), () => SelectBlockSettingsView("storage"),
                ElementBounds.Fixed(214, 210, 168, 30), blockSettingsView == "storage")
            .AddSuiteButton(SuiteLocalization.Get("gui.workspace.queries"), () => SelectBlockSettingsView("queries"),
                ElementBounds.Fixed(394, 210, 168, 30), blockSettingsView == "queries")
            .AddRichtext(SuiteLocalization.Get("gui.blenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(738, 215, 200, 24))
            .AddSwitch(value => SetBlockLoggerBool("enabled", value), ElementBounds.Fixed(956, 210, 60, 30), "bl-enabled")
            .AddHoverText(SuiteLocalization.Get("gui.tip.more.4361c09efd"), CairoFont.WhiteSmallText(), 390,
                ElementBounds.Fixed(956, 210, 60, 30), "tip-bl-enabled");

        if (blockSettingsView == "events")
        {
            AddSettingsCard(composer, "gui.blockevents", 28, 264, 492, 222);
            AddSettingsCard(composer, "gui.containersheading", 536, 264, 492, 222);
            AddBlockToggle(composer, "gui.break", "logblockbreak", "bl-break", 44, 318);
            AddBlockToggle(composer, "gui.place", "logblockplace", "bl-place", 44, 362);
            AddBlockToggle(composer, "gui.use", "logblockuse", "bl-use", 44, 406);
            AddBlockToggle(composer, "gui.access", "logcontaineraccess", "bl-container-access", 552, 318);
            AddBlockToggle(composer, "gui.itemchanges", "logcontaineritemchanges", "bl-container-items", 552, 362);

            AddSettingsCard(composer, "gui.entityevents", 28, 502, 1000, 164);
            AddBlockToggle(composer, "gui.interact", "logentityinteract", "bl-entity-interact", 44, 548, 264);
            AddBlockToggle(composer, "gui.spawn", "logentityspawn", "bl-entity-spawn", 44, 592, 264);
            AddBlockToggle(composer, "gui.death", "logentitydeath", "bl-entity-death", 374, 548, 264);
            AddBlockToggle(composer, "gui.despawn", "logentitydespawn", "bl-entity-despawn", 374, 592, 264);
            AddBlockToggle(composer, "gui.aggregate", "aggregateentityspawndespawn", "bl-aggregate", 708, 548, 264);
        }
        else if (blockSettingsView == "storage")
        {
            AddSettingsCard(composer, "gui.workspace.retention", 28, 264, 1000, 118);
            AddBlockNumber(composer, "gui.retentiondays", "bl-retention", OnBlockRetentionChanged, SaveBlockRetention, 44, 318, "gui.tip.more.50ed193127");
            AddSettingsCard(composer, "gui.workspace.writer", 28, 398, 1000, 222);
            AddBlockNumber(composer, "gui.flushinterval", "bl-flush", OnBlockFlushChanged, SaveBlockFlush, 44, 450, "gui.tip.more.052803c007");
            AddBlockNumber(composer, "gui.maxqueue", "bl-queue", OnBlockQueueChanged, SaveBlockQueue, 552, 450, "gui.tip.more.e84f3495d3");
            AddBlockNumber(composer, "gui.emergencyoverflow", "bl-overflow", OnBlockOverflowChanged, SaveBlockOverflow, 44, 502, "gui.tip.more.f02bb317d5");
        }
        else
        {
            AddSettingsCard(composer, "gui.workspace.radius", 28, 264, 1000, 170);
            AddBlockNumber(composer, "gui.defaultblockradius", "bl-default-radius", OnBlockDefaultRadiusChanged, SaveBlockDefaultRadius, 44, 316);
            AddBlockNumber(composer, "gui.defaultentityradius", "bl-entity-radius", OnBlockEntityRadiusChanged, SaveBlockEntityRadius, 552, 316);
            AddBlockNumber(composer, "gui.maximumradius", "bl-max-radius", OnBlockMaxRadiusChanged, SaveBlockMaxRadius, 44, 368);
            AddSettingsCard(composer, "gui.workspace.rows", 28, 450, 1000, 170);
            AddBlockNumber(composer, "gui.defaultrows", "bl-default-rows", OnBlockDefaultRowsChanged, SaveBlockDefaultRows, 44, 502);
            AddBlockNumber(composer, "gui.defaultentityrows", "bl-default-entity-rows", OnBlockDefaultEntityRowsChanged, SaveBlockDefaultEntityRows, 552, 502);
            AddBlockNumber(composer, "gui.maxresults", "bl-max-results", OnBlockMaxResultsChanged, SaveBlockMaxResults, 44, 554, "gui.tip.more.89f406f6e0");
        }

        composer.AddStaticText(SuiteLocalization.Get("gui.blockloggerlowlevelnote"), CairoFont.WhiteSmallText().WithColor(new[] { 0.67, 0.74, 0.82, 1.0 }),
            ElementBounds.Fixed(34, 690, 980, 40));
    }

    private void AddSettingsCard(GuiComposer composer, string titleKey, double x, double y, double width, double height)
    {
        composer
            .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(x, y, width, height)))
            .AddRichtext(SuiteLocalization.Get(titleKey), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }),
                ElementBounds.Fixed(x + 16, y + 15, width - 32, 26));
    }

    private void BuildAntiCheatClientSettings(GuiComposer composer)
    {
        composer
            .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(34, 132, 690, 26), "page-header")
            .AddSuiteButton(SuiteLocalization.Get("gui.literal.0022a3e10c"), SaveAntiCheatSettings, ElementBounds.Fixed(770, 128, 136, 30))
            .AddHoverText(SuiteLocalization.Get("gui.saveallsettings"), CairoFont.WhiteSmallText(), 380,
                ElementBounds.Fixed(770, 128, 136, 30), "tip-anticheat-settings-save")
            .AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RequestAntiCheat("settings"), ElementBounds.Fixed(918, 128, 110, 30))
            .AddRichtext(SuiteLocalization.Get("gui.settingsstagedhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, 174, 980, 40));

        AddSettingsCard(composer, "gui.clientscanner", 28, 234, 1000, 238);
        composer
            .AddRichtext(SuiteLocalization.Get("gui.requireclient"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, 288, 332, 28))
            .AddSwitch(v => SetAntiCheatSettingBool("requireclient", v), ElementBounds.Fixed(452, 282, 52, 26), "ac-set-require-client")
            .AddRichtext(SuiteLocalization.Get("gui.periodicrescan"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, 288, 318, 28))
            .AddNumberInput(ElementBounds.Fixed(912, 282, 96, 28), v => SetAntiCheatSettingInt("rescan", v), CairoFont.WhiteSmallText(), "ac-set-rescan")
            .AddRichtext(SuiteLocalization.Get("gui.rescanruntime"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, 332, 332, 28))
            .AddSwitch(v => SetAntiCheatSettingBool("runtime-rescan", v), ElementBounds.Fixed(452, 326, 52, 26), "ac-set-runtime-rescan")
            .AddRichtext(SuiteLocalization.Get("gui.rescancooldown"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, 332, 318, 28))
            .AddNumberInput(ElementBounds.Fixed(912, 326, 96, 28), v => SetAntiCheatSettingInt("runtime-cooldown", v), CairoFont.WhiteSmallText(), "ac-set-runtime-cooldown")
            .AddRichtext(SuiteLocalization.Get("gui.similarity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, 376, 332, 28))
            .AddSwitch(v => SetAntiCheatSettingBool("similarity", v), ElementBounds.Fixed(452, 370, 52, 26), "ac-set-similarity")
            .AddRichtext(SuiteLocalization.Get("gui.similaritythresholds"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, 376, 300, 28))
            .AddRichtext(SuiteLocalization.Get("gui.reviewplain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, 419, 84, 26))
            .AddNumberInput(ElementBounds.Fixed(640, 413, 96, 28), v => SetAntiCheatSettingInt("similarity-review", v), CairoFont.WhiteSmallText(), "ac-set-sim-review")
            .AddRichtext(SuiteLocalization.Get("gui.literal.b1a5954a48"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(818, 419, 84, 26))
            .AddNumberInput(ElementBounds.Fixed(912, 413, 96, 28), v => SetAntiCheatSettingInt("similarity-high", v), CairoFont.WhiteSmallText(), "ac-set-sim-high");

        AddSettingsCard(composer, "gui.packetprotectiontitle", 28, 492, 1000, 172);
        composer
            .AddRichtext(SuiteLocalization.Get("gui.packetsettingsmovedhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, 548, 716, 66))
            .AddSuiteButton(SuiteLocalization.Get("gui.packetprotection"), () => SelectAntiCheatView("packet"), ElementBounds.Fixed(790, 544, 218, 32))
            .AddRichtext(SuiteLocalization.Get("gui.packetsettingssavehelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, 614, 944, 40))
            .AddRichtext(SuiteLocalization.Get("gui.clientsettingssavehelp"), CairoFont.WhiteSmallText().WithColor(new[] { 0.67, 0.74, 0.82, 1.0 }),
                ElementBounds.Fixed(34, 692, 980, 38));
    }

    private void AddBlockToggle(GuiComposer composer, string labelKey, string setting, string id, double x, double y, double width = 460)
    {
        composer
            .AddRichtext(SuiteLocalization.Get(labelKey), CairoFont.WhiteSmallText(), ElementBounds.Fixed(x, y + 5, width - 74, 28))
            .AddSwitch(value => SetBlockLoggerBool(setting, value), ElementBounds.Fixed(x + width - 60, y, 60, 30), id);
    }

    private void AddBlockNumber(GuiComposer composer, string labelKey, string id, Action<string> onChange, ActionConsumable onSave,
        double x, double y, string? helpKey = null)
    {
        composer
            .AddRichtext(SuiteLocalization.Get(labelKey), CairoFont.WhiteSmallText(), ElementBounds.Fixed(x, y + 5, 260, 34))
            .AddNumberInput(ElementBounds.Fixed(x + 266, y, 96, 30), onChange, CairoFont.WhiteSmallText(), id)
            .AddSuiteButton(SuiteLocalization.Get("gui.save"), onSave, ElementBounds.Fixed(x + 374, y, 86, 30));
        if (helpKey != null)
            composer.AddHoverText(SuiteLocalization.Get(helpKey), CairoFont.WhiteSmallText(), 410,
                ElementBounds.Fixed(x + 266, y, 96, 30), "tip-" + id);
    }
}
