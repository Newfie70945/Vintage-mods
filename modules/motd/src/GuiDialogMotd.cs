using System;
using System.Diagnostics;
using System.IO;
using Cairo;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace NewfiesCommunityMOTD
{
    public class GuiDialogMotd : GuiDialog
    {
        private MotdPacket packet;
        private readonly Action<bool> onClosedCb;
        private readonly Action<string, MotdPacket> onFeedOpenedCb;
        private int unreadAnnouncements;
        private int unreadUpdates;
        private float baseTextY;
        private float baseTextY2;
        private bool isCheckboxChecked;
        private string activeTab = "home";
        private CairoFont? textFont;
        private ElementBounds? textBounds;
        private ElementBounds? textBounds2;
        private float scrollViewportHeight;
        private float scrollViewportHeight2;
        private string? serverLogoTempPath;

        public bool DontShowAgainChecked => isCheckboxChecked;
        public override string ToggleKeyCombinationCode => "motd:dialog";

        public GuiDialogMotd(ICoreClientAPI capi, MotdPacket packet, Action<bool> onClosed, Action<string, MotdPacket> onFeedOpened, int unreadAnnouncements, int unreadUpdates) : base(capi)
        {
            this.packet = packet;
            packet.ContentVtml = NormalizeVtml(packet.ContentVtml ?? "");
            onClosedCb = onClosed ?? (_ => { });
            onFeedOpenedCb = onFeedOpened ?? ((_, __) => { });
            this.unreadAnnouncements = unreadAnnouncements;
            this.unreadUpdates = unreadUpdates;
            ComposeDialog();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            CleanupServerLogoTempFile();
            ClearComposers();
            onClosedCb(DontShowAgainChecked);
        }

        private void CleanupServerLogoTempFile()
        {
            if (string.IsNullOrWhiteSpace(serverLogoTempPath)) return;
            try { File.Delete(serverLogoTempPath); } catch { }
            serverLogoTempPath = null;
        }

        private static string NormalizeVtml(string s) => MotdFeedFormatter.Normalize(s);

        private void ComposeDialog()
        {
            // Assigning SingleComposer replaces the dictionary entry without disposing
            // the old Cairo/GL textures. Tabs and refreshes must release it first.
            ClearComposers();
            CleanupServerLogoTempFile();
            // Responsive sizing: packet dimensions are the preferred size, not an absolute size.
            // Convert the framebuffer to unscaled GUI units, reserve a safety margin, then clamp
            // the MOTD so high GUI scales and smaller windows cannot push controls off-screen.
            double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
            int availableWidth = Math.Max(620, (int)Math.Floor(capi.Render.FrameWidth / guiScale) - 72);
            int availableHeight = Math.Max(480, (int)Math.Floor(capi.Render.FrameHeight / guiScale) - 112);
            int desiredWidth = GameMath.Clamp(Math.Max(packet.Width, 760), 620, availableWidth);
            int desiredHeight = GameMath.Clamp(Math.Max(packet.Height, 620), 520, availableHeight);
            int scrollBarWidth = GameMath.Clamp(packet.ScrollBarWidth <= 0 ? 18 : packet.ScrollBarWidth, 10, 30);
            bool hasServerLogo = packet.EnableLogo && string.Equals(packet.LogoSource, "ServerFile", StringComparison.OrdinalIgnoreCase) && packet.ServerLogoPng != null && packet.ServerLogoPng.Length > 0;
            bool hasAssetLogo = packet.EnableLogo && !hasServerLogo && !string.IsNullOrWhiteSpace(packet.LogoAsset) && LogoExists(packet.LogoAsset);
            bool hasLogo = hasServerLogo || hasAssetLogo;
            int logoH = hasLogo ? GameMath.Clamp(packet.LogoHeight, 0, 400) : 0;
            const int sidePad = 24;
            int innerPad = activeTab is "news" or "updates" ? 20 : 12;
            const int titleRowH = 96;
            // Tabs tighten slightly on narrower responsive layouts while retaining a comfortable
            // click target. Normal Vintage Story buttons provide the native subtle hover brighten;
            // the selected inset/dot remains stronger so hover never competes with active identity.
            int tabW = desiredWidth < 720 ? 100 : 112;
            int tabGap = desiredWidth < 650 ? 5 : 7;
            const int tabRowH = 34;

            int enabledTabs = 1
                + (packet.EnableAnnouncementsTab ? 1 : 0)
                + (packet.EnableUpdatesTab ? 1 : 0)
                + (packet.EnableEventsTab ? 1 : 0)
                + (packet.EnableRulesTab ? 1 : 0)
                + (packet.EnableServerTab ? 1 : 0)
                + (packet.EnableCommandsTab ? 1 : 0)
                + 0;
            int tabsPerRow = Math.Max(1, (desiredWidth - sidePad * 2 + tabGap) / (tabW + tabGap));
            int tabRows = HasAnyTabs() ? (int)Math.Ceiling(enabledTabs / (double)tabsPerRow) : 0;
            int tabsH = tabRows * tabRowH;
            int subtitleH = 0; // Subtitle is rendered inside the modern header card.
            int infoStripH = HasAnyTabs() ? 38 : 0;
            int headerH = titleRowH + subtitleH + tabsH + infoStripH + 12;
            int bottomPad = packet.EnableDontShowAgain ? 122 : 92;

            int naturalViewportH = desiredHeight - (headerH + bottomPad);
            int viewportH = packet.ScrollOverrideHeightPx > 0
                ? Math.Min(packet.ScrollOverrideHeightPx, naturalViewportH)
                : naturalViewportH;
            viewportH = Math.Max(120, Math.Min(viewportH, desiredHeight - headerH - bottomPad));
            int viewportW = desiredWidth - (sidePad * 2) - scrollBarWidth - 8;

            var dialogBounds = ElementBounds.Fixed(0, 0, desiredWidth, desiredHeight);

            string dialogTitle = string.IsNullOrWhiteSpace(packet.Title) ? "Message of the Day" : packet.Title;
            var titleFont = CairoFont.WhiteSmallText()
                .WithWeight(FontWeight.Bold)
                .WithOrientation(EnumTextOrientation.Left)
                .WithColor(new double[] { 0.941, 0.820, 0.541, 1 });
            titleFont.UnscaledFontsize = Math.Max(titleFont.UnscaledFontsize, 22);

            var subtitleFont = CairoFont.WhiteSmallText()
                .WithOrientation(EnumTextOrientation.Left)
                .WithColor(new double[] { 0.57, 0.66, 0.71, 1 });

            var onlineFont = CairoFont.WhiteSmallText()
                .WithWeight(FontWeight.Bold)
                .WithOrientation(EnumTextOrientation.Right)
                .WithColor(new double[] { 0.494, 0.906, 0.529, 1 });
            onlineFont.UnscaledFontsize = Math.Max(onlineFont.UnscaledFontsize, 15);

            var playerCountFont = CairoFont.WhiteSmallText()
                .WithOrientation(EnumTextOrientation.Right)
                .WithColor(new double[] { 0.57, 0.66, 0.71, 1 });

            // Transparent button hitboxes sit over custom rounded surfaces so the MOTD does not
            // inherit Vintage Story's default brown button texture.
            var tabFont = CairoFont.SmallButtonText(EnumButtonStyle.None)
                .WithColor(new double[] { 0.765, 0.816, 0.839, 1 });
            var tabActiveFont = CairoFont.SmallButtonText(EnumButtonStyle.None)
                .WithWeight(FontWeight.Bold)
                .WithColor(new double[] { 1, 1, 1, 1 });
            var closeFont = CairoFont.SmallButtonText(EnumButtonStyle.None)
                .WithWeight(FontWeight.Bold)
                .WithColor(new double[] { 0.94, 0.82, 0.82, 1 });
            var footerButtonFont = CairoFont.SmallButtonText(EnumButtonStyle.None)
                .WithWeight(FontWeight.Bold)
                .WithColor(new double[] { 0.87, 0.91, 0.93, 1 });
            var discordButtonFont = CairoFont.SmallButtonText(EnumButtonStyle.None)
                .WithWeight(FontWeight.Bold)
                .WithColor(new double[] { 0.86, 0.88, 1.0, 1 });
            var okButtonFont = CairoFont.SmallButtonText(EnumButtonStyle.None)
                .WithWeight(FontWeight.Bold)
                .WithColor(new double[] { 0.95, 0.82, 0.55, 1 });

            double headerStatusW = 180;
            double headerStatusX = desiredWidth - sidePad - 80 - headerStatusW;
            double headerTitleX = sidePad + 14;
            double headerTitleW = Math.Max(220, headerStatusX - headerTitleX - 16);

            // Custom-drawn shell: dark, rounded, and much closer to the supplied HTML mockup.
            var compo = capi.Gui.CreateCompo("motd-dialog", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
                .BeginChildElements(dialogBounds)
                .AddStaticCustomDraw(ElementBounds.Fixed(6, 6, desiredWidth - 12, desiredHeight - 12),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 14, 0.067, 0.094, 0.125, 0.985, 0.196, 0.267, 0.322, 1, 1.2))
                .AddStaticCustomDraw(ElementBounds.Fixed(sidePad - 4, 14, desiredWidth - (sidePad - 4) * 2, 68),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 10, 0.106, 0.161, 0.184, 0.985, 0.231, 0.314, 0.357, 1, 1.0, 0.788, 0.604, 0.282, 1, 2, true))
                .AddStaticText(dialogTitle, titleFont, ElementBounds.Fixed(headerTitleX, 24, headerTitleW, 28), "dialogtitle")
                .AddStaticText(string.IsNullOrWhiteSpace(packet.Subtitle) ? "" : packet.Subtitle,
                    subtitleFont, ElementBounds.Fixed(headerTitleX, 51, headerTitleW, 18), "subtitle")
                .AddStaticCustomDraw(ElementBounds.Fixed(headerStatusX - 10, 20, headerStatusW + 22, 50),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 7, 0.114, 0.169, 0.133, 0.96, 0.349, 0.596, 0.400, 1, 1.0))
                .AddStaticText("● SERVER ONLINE", onlineFont,
                    ElementBounds.Fixed(headerStatusX, 27, headerStatusW, 18), "serveronline")
                .AddStaticText($"{packet.OnlineCount} / {packet.MaxPlayers} Players", playerCountFont,
                    ElementBounds.Fixed(headerStatusX, 50, headerStatusW, 16), "headerplayers")
                .AddStaticCustomDraw(ElementBounds.Fixed(desiredWidth - sidePad - 38, 24, 30, 28),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 7, 0.165, 0.102, 0.102, 0.96, 0.486, 0.290, 0.290, 1, 1.0))
                .AddButton("X", OnCloseButton, ElementBounds.Fixed(desiredWidth - sidePad - 38, 24, 30, 28),
                    closeFont, EnumButtonStyle.None, "closebtn");

            double y = titleRowH;

            if (HasAnyTabs())
            {
                // A shallow navigation tray visually groups wrapped tab rows while retaining
                // native buttons/hover behavior. It intentionally stays subtle.
                compo.AddStaticCustomDraw(ElementBounds.Fixed(sidePad - 5, y - 4, desiredWidth - (sidePad - 5) * 2, tabsH + 10),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 8, 0.067, 0.106, 0.129, 0.985, 0.200, 0.275, 0.310, 1, 1.0));
                // Center each navigation row so the tabs read as a single deliberate control bar.
                int rowCapacity = Math.Max(1, tabsPerRow);
                int remainingTabs = enabledTabs;
                int remainingRows = Math.Max(1, tabRows);
                // Balance wrapped navigation instead of greedily filling the first row.
                // Example: seven tabs over two rows becomes 4 + 3 rather than 6 + 1.
                int currentRowCount = Math.Min(rowCapacity, (int)Math.Ceiling(remainingTabs / (double)remainingRows));
                double tx = Math.Max(sidePad, (desiredWidth - (currentRowCount * tabW + Math.Max(0, currentRowCount - 1) * tabGap)) / 2.0);
                double ty = y;
                int tabIndexInRow = 0;
                void Tab(string id, string label, bool enabled)
                {
                    if (!enabled) return;
                    if (tabIndexInRow >= currentRowCount)
                    {
                        remainingTabs -= currentRowCount;
                        remainingRows = Math.Max(1, remainingRows - 1);
                        currentRowCount = Math.Min(rowCapacity, (int)Math.Ceiling(remainingTabs / (double)remainingRows));
                        tx = Math.Max(sidePad, (desiredWidth - (currentRowCount * tabW + Math.Max(0, currentRowCount - 1) * tabGap)) / 2.0);
                        ty += tabRowH;
                        tabIndexInRow = 0;
                    }
                    bool isActive = activeTab == id;
                    var tabBounds = ElementBounds.Fixed(tx, ty, tabW, 28);
                    if (isActive)
                    {
                        compo.AddStaticCustomDraw(ElementBounds.Fixed(tx - 2, ty - 1, tabW + 4, 30),
                            (ctx, _, b) => DrawRoundedPanel(ctx, b, 6, 0.157, 0.314, 0.416, 0.98, 0.361, 0.659, 0.839, 1, 1.2));
                    }
                    else
                    {
                        compo.AddStaticCustomDraw(ElementBounds.Fixed(tx - 2, ty - 1, tabW + 4, 30),
                            (ctx, _, b) => DrawRoundedPanel(ctx, b, 6, 0.090, 0.141, 0.173, 0.96, 0.204, 0.290, 0.333, 1, 1.0));
                    }
                    compo.AddButton(label, () => { SwitchTab(id); return true; }, tabBounds,
                        isActive ? tabActiveFont : tabFont, EnumButtonStyle.None, "tab" + id);
                    tx += tabW + tabGap;
                    tabIndexInRow++;
                }
                Tab("home", "Home", true);
                Tab("news", unreadAnnouncements > 0 ? $"News ({unreadAnnouncements})" : "News", packet.EnableAnnouncementsTab);
                Tab("updates", unreadUpdates > 0 ? $"Updates ({unreadUpdates})" : "Updates", packet.EnableUpdatesTab);
                Tab("events", "Events", packet.EnableEventsTab);
                Tab("rules", "Rules", packet.EnableRulesTab);
                Tab("server", "Server Info", packet.EnableServerTab);
                Tab("commands", "Commands", packet.EnableCommandsTab);
                y += tabsH;

                // Persistent at-a-glance information belongs below the navigation, not in its own page.
                string date = string.IsNullOrWhiteSpace(packet.InGameDate) ? "Unavailable" : packet.InGameDate;
                string time = string.IsNullOrWhiteSpace(packet.InGameTime) ? "" : $" · {packet.InGameTime}";
                string season = string.IsNullOrWhiteSpace(packet.Season) ? "" : $"   |   {packet.Season}";
                string info = $"{date}{time}   |   {packet.OnlineCount} / {packet.MaxPlayers} Players{season}";
                var infoFont = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
                y += 4;
                compo.AddStaticCustomDraw(ElementBounds.Fixed(sidePad, y + 1, desiredWidth - sidePad * 2, 30),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 8, 0.063, 0.145, 0.212, 0.98, 0.361, 0.659, 0.839, 1, 1.0));
                compo.AddStaticText(info, infoFont, ElementBounds.Fixed(sidePad + 12, y + 8, desiredWidth - sidePad * 2 - 24, 18), "infostrip");
                y += infoStripH;
            }

            y += 8;
            var panelBounds = ElementBounds.Fixed(sidePad, y, viewportW, viewportH);
            var clipBounds = panelBounds.ForkContainingChild(innerPad, innerPad, innerPad, innerPad);

            // The logo belongs to Home content, not the global dialog header. Keep it in a small,
            // centered landing-page box, capped at 400 x 400 GUI units, so it can never crowd or overlap navigation. The
            // {{serverlogo}} token is treated as the Home placement marker and removed from VTML;
            // legacy configs with EnableLogo=true still receive the same top-of-Home placement.
            bool showHomeLogo = activeTab == "home" && hasLogo;
            int requestedLogoW = GameMath.Clamp(packet.LogoWidth, 0, 400);
            int requestedLogoH = GameMath.Clamp(packet.LogoHeight, 0, 400);
            int homeLogoW = showHomeLogo ? (requestedLogoW == 0 ? 400 : requestedLogoW) : 0;
            int homeLogoH = showHomeLogo ? (requestedLogoH == 0 ? 400 : requestedLogoH) : 0;
            if (showHomeLogo && hasServerLogo && packet.LogoPreserveAspectRatio)
            {
                try
                {
                    byte[] png = packet.ServerLogoPng!;
                    int sourceW = png.Length >= 24 ? (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19] : 1;
                    int sourceH = png.Length >= 24 ? (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23] : 1;
                    double aspect = Math.Max(1, sourceW) / (double)Math.Max(1, sourceH);
                    if (requestedLogoW == 0 && requestedLogoH > 0) homeLogoW = Math.Min(400, Math.Max(1, (int)Math.Round(homeLogoH * aspect)));
                    else if (requestedLogoH == 0 && requestedLogoW > 0) homeLogoH = Math.Min(400, Math.Max(1, (int)Math.Round(homeLogoW / aspect)));
                    else if (requestedLogoW == 0 && requestedLogoH == 0)
                    {
                        if (aspect >= 1) { homeLogoW = 400; homeLogoH = Math.Max(1, (int)Math.Round(400 / aspect)); }
                        else { homeLogoH = 400; homeLogoW = Math.Max(1, (int)Math.Round(400 * aspect)); }
                    }
                }
                catch { }
            }
            int homeLogoGap = showHomeLogo ? 12 : 0;
            scrollViewportHeight = (float)Math.Max(40, clipBounds.fixedHeight - innerPad * 2);
            textBounds = ElementBounds.Fixed(clipBounds.fixedX + innerPad, clipBounds.fixedY + innerPad,
                clipBounds.fixedWidth - innerPad * 2, scrollViewportHeight);
            var scrollbarBounds = ElementBounds.Fixed(sidePad + viewportW + 5, y + 2, scrollBarWidth, viewportH - 4);

            textFont = CairoFont.WhiteSmallText();
            if (activeTab is "news" or "updates") textFont.UnscaledFontsize = 16;
            if (activeTab == "server" && packet.EnableServerTab)
            {
                // Server Info gets a purpose-built two-column dashboard: server/gameplay on the
                // left, world/prospecting on the right. Each column scrolls independently.
                double gap = 16;
                double colW = (desiredWidth - sidePad * 2 - gap) / 2.0;
                double colTextW = colW - scrollBarWidth - innerPad * 3;
                var leftPanel = ElementBounds.Fixed(sidePad, y, colW, viewportH);
                var rightPanel = ElementBounds.Fixed(sidePad + colW + gap, y, colW, viewportH);
                var leftClip = leftPanel.ForkContainingChild(innerPad, innerPad, innerPad, innerPad);
                var rightClip = rightPanel.ForkContainingChild(innerPad, innerPad, innerPad, innerPad);
                textBounds = ElementBounds.Fixed(leftClip.fixedX + innerPad, leftClip.fixedY + innerPad, colTextW, leftClip.fixedHeight - innerPad * 2);
                textBounds2 = ElementBounds.Fixed(rightClip.fixedX + innerPad, rightClip.fixedY + innerPad, colTextW, rightClip.fixedHeight - innerPad * 2);
                scrollViewportHeight = (float)Math.Max(40, leftClip.fixedHeight - innerPad * 2);
                scrollViewportHeight2 = (float)Math.Max(40, rightClip.fixedHeight - innerPad * 2);
                compo.AddStaticCustomDraw(leftPanel,
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 9, 0.055, 0.082, 0.102, 0.98, 0.263, 0.349, 0.392, 1, 1.0, 0.361, 0.659, 0.839, 1, 3, false));
                compo.BeginClip(leftClip); compo.AddRichtext(packet.ServerInfoLeftVtml, textFont, textBounds, "text"); compo.EndClip();
                compo.AddVerticalScrollbar(OnScroll, ElementBounds.Fixed(sidePad + colW - scrollBarWidth - 5, y + 2, scrollBarWidth, viewportH - 4), "vsb");
                compo.AddStaticCustomDraw(rightPanel,
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 9, 0.055, 0.082, 0.102, 0.98, 0.263, 0.349, 0.392, 1, 1.0, 0.788, 0.604, 0.282, 1, 3, false));
                compo.BeginClip(rightClip); compo.AddRichtext(packet.ServerInfoRightVtml, textFont, textBounds2, "text2"); compo.EndClip();
                compo.AddVerticalScrollbar(OnScroll2, ElementBounds.Fixed(sidePad + colW + gap + colW - scrollBarWidth - 5, y + 2, scrollBarWidth, viewportH - 4), "vsb2");
            }
            else
            {
                textBounds2 = null;
                scrollViewportHeight2 = 0f;
                compo.AddStaticCustomDraw(panelBounds,
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 10, 0.047, 0.071, 0.086, 0.98, 0.263, 0.349, 0.392, 1, 1.0, GetAccentForTab(activeTab).Item1, GetAccentForTab(activeTab).Item2, GetAccentForTab(activeTab).Item3, 1, 3, false));
                compo.BeginClip(clipBounds);

                string activeVtml = GetActiveVtml();
                if (activeTab == "home")
                    activeVtml = Regex.Replace(activeVtml ?? "", @"\{\{serverlogo\}\}", "", RegexOptions.IgnoreCase);

                if (showHomeLogo)
                {
                    string? logoPath = PrepareLogoTempFile(hasServerLogo);
                    if (!string.IsNullOrWhiteSpace(logoPath))
                    {
                        var textComponents = VtmlUtil.Richtextify(capi, activeVtml ?? "", textFont);
                        var components = new RichTextComponentBase[textComponents.Length + 1];
                        components[0] = new MotdLogoTextComponent(
                            capi, logoPath!, homeLogoW, homeLogoH, homeLogoGap, packet.LogoPreserveAspectRatio);
                        Array.Copy(textComponents, 0, components, 1, textComponents.Length);
                        compo.AddRichtext(components, textBounds, "text");
                    }
                    else
                    {
                        compo.AddRichtext(activeVtml, textFont, textBounds, "text");
                    }
                }
                else
                {
                    compo.AddRichtext(activeVtml, textFont, textBounds, "text");
                }
                compo.EndClip();
                compo.AddVerticalScrollbar(OnScroll, scrollbarBounds, "vsb");
            }

            // Footer: one reserved row below the content viewport. Nothing in the scrollable
            // content is allowed to extend into this area.
            double contentBottom = y + viewportH;
            double dividerY = contentBottom + 7;
            double footerTop = contentBottom + 16;
            double footerTrayH = 58;

            // Divider belongs in the breathing room between the reading surface and footer,
            // not inside the footer tray itself.
            compo.AddStaticCustomDraw(ElementBounds.Fixed(sidePad, dividerY, desiredWidth - sidePad * 2, 2),
                (ctx, _, b) => DrawPanel(ctx, b, 0.788, 0.604, 0.282, 1, 0.788, 0.604, 0.282, 1, 0));
            compo.AddStaticCustomDraw(ElementBounds.Fixed(sidePad - 5, footerTop, desiredWidth - (sidePad - 5) * 2, footerTrayH),
                (ctx, _, b) => DrawRoundedPanel(ctx, b, 8, 0.067, 0.106, 0.129, 0.98, 0.200, 0.275, 0.310, 1, 1.0));

            double buttonY = footerTop + 16;
            double okW = 90;
            double actionW = 154;
            double actionGap = 14;
            double okX = desiredWidth - sidePad - okW;

            if (packet.EnableDontShowAgain)
            {
                compo.AddSwitch(OnDontShowAgainToggled, ElementBounds.Fixed(sidePad, buttonY + 3, 32, 24), "dontshowagain", 22, 2);
                compo.AddStaticCustomDraw(ElementBounds.Fixed(sidePad + 38, buttonY + 2, 180, 26),
                    (ctx, _, b) => DrawRoundedPanel(ctx, b, 6, 0.075, 0.114, 0.137, 0.92, 0.204, 0.290, 0.333, 1, 1.0));
                compo.AddButton(Lang.Get("Don't show again"), ToggleDontShowAgainFromLabel,
                    ElementBounds.Fixed(sidePad + 38, buttonY + 2, 180, 26), footerButtonFont, EnumButtonStyle.None, "dontshowagainlabelbtn");
            }

            bool showLink = packet.EnableLinkButton && !string.IsNullOrWhiteSpace(packet.LinkUrl);
            bool showMap = packet.EnableWebMapButton && !string.IsNullOrWhiteSpace(packet.WebMapUrl);
            int externalCount = (showLink ? 1 : 0) + (showMap ? 1 : 0);
            if (externalCount > 0)
            {
                double groupW = externalCount * actionW + Math.Max(0, externalCount - 1) * actionGap;
                double actionX = (desiredWidth - groupW) / 2.0;
                if (showLink)
                {
                    compo.AddStaticCustomDraw(ElementBounds.Fixed(actionX, buttonY, actionW, 30),
                        (ctx, _, b) => DrawRoundedPanel(ctx, b, 6, 0.153, 0.192, 0.443, 0.96, 0.345, 0.396, 0.949, 1, 1.2));
                    compo.AddButton(string.IsNullOrWhiteSpace(packet.LinkButtonText) ? "Open Link" : packet.LinkButtonText,
                        OnLinkClicked, ElementBounds.Fixed(actionX, buttonY, actionW, 30),
                        discordButtonFont, EnumButtonStyle.None, "linkbtn");
                    actionX += actionW + actionGap;
                }
                if (showMap)
                {
                    compo.AddStaticCustomDraw(ElementBounds.Fixed(actionX, buttonY, actionW, 30),
                        (ctx, _, b) => DrawRoundedPanel(ctx, b, 6, 0.090, 0.196, 0.286, 0.96, 0.361, 0.659, 0.839, 1, 1.2));
                    compo.AddButton(string.IsNullOrWhiteSpace(packet.WebMapButtonText) ? "Web Map" : packet.WebMapButtonText,
                        OnWebMapClicked, ElementBounds.Fixed(actionX, buttonY, actionW, 30),
                        footerButtonFont, EnumButtonStyle.None, "webmapbtn");
                }
            }

            compo.AddStaticCustomDraw(ElementBounds.Fixed(okX, buttonY, okW, 30),
                (ctx, _, b) => DrawRoundedPanel(ctx, b, 6, 0.231, 0.188, 0.125, 0.98, 0.788, 0.604, 0.282, 1, 1.2));
            compo.AddButton(Lang.Get("Ok"), OnOk, ElementBounds.Fixed(okX, buttonY, okW, 30),
                okButtonFont, EnumButtonStyle.None, "okbtn");

            compo.EndChildElements();
            SingleComposer = compo.Compose();
            var checkbox = SingleComposer.GetSwitch("dontshowagain");
            if (checkbox != null) checkbox.On = isCheckboxChecked;
            ResetScroll();
        }

        public override void Dispose()
        {
            CleanupServerLogoTempFile();
            ClearComposers();
            base.Dispose();
        }

        private static (double, double, double) GetAccentForTab(string? tab)
        {
            return (tab ?? "home") switch
            {
                "news" => (0.824, 0.643, 0.310),
                "updates" => (0.475, 0.718, 0.357),
                "events" => (0.361, 0.659, 0.839),
                "rules" => (0.831, 0.416, 0.416),
                "commands" => (0.839, 0.706, 0.361),
                "server" => (0.361, 0.659, 0.839),
                _ => (0.788, 0.604, 0.282)
            };
        }

        private static void RoundedRectPath(Context ctx, double x, double y, double width, double height, double radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2.0));
            if (radius <= 0.1)
            {
                ctx.Rectangle(x, y, width, height);
                return;
            }

            double x2 = x + width;
            double y2 = y + height;
            ctx.NewSubPath();
            ctx.Arc(x2 - radius, y + radius, radius, -Math.PI / 2, 0);
            ctx.Arc(x2 - radius, y2 - radius, radius, 0, Math.PI / 2);
            ctx.Arc(x + radius, y2 - radius, radius, Math.PI / 2, Math.PI);
            ctx.Arc(x + radius, y + radius, radius, Math.PI, 3 * Math.PI / 2);
            ctx.ClosePath();
        }

        private static void DrawRoundedPanel(
            Context ctx,
            ElementBounds bounds,
            double radius,
            double fillR, double fillG, double fillB, double fillA,
            double borderR, double borderG, double borderB, double borderA,
            double borderWidth,
            double accentR = 0, double accentG = 0, double accentB = 0, double accentA = 0,
            double accentSize = 0,
            bool accentAtBottom = false)
        {
            ctx.Save();

            double x = bounds.drawX;
            double y = bounds.drawY;
            double w = bounds.InnerWidth;
            double h = bounds.InnerHeight;
            if (w <= 0 || h <= 0)
            {
                ctx.Restore();
                return;
            }

            RoundedRectPath(ctx, x, y, w, h, radius);
            ctx.SetSourceRGBA(fillR, fillG, fillB, fillA);
            ctx.FillPreserve();

            if (accentA > 0 && accentSize > 0)
            {
                ctx.Save();
                ctx.Clip();
                ctx.SetSourceRGBA(accentR, accentG, accentB, accentA);
                if (accentAtBottom)
                    ctx.Rectangle(x, y + h - accentSize, w, accentSize);
                else
                    ctx.Rectangle(x, y, w, accentSize);
                ctx.Fill();
                ctx.Restore();

                RoundedRectPath(ctx, x, y, w, h, radius);
            }

            if (borderWidth > 0 && borderA > 0)
            {
                ctx.SetSourceRGBA(borderR, borderG, borderB, borderA);
                ctx.LineWidth = borderWidth;
                double inset = borderWidth / 2.0;
                RoundedRectPath(ctx, x + inset, y + inset, Math.Max(0, w - borderWidth), Math.Max(0, h - borderWidth), Math.Max(0, radius - inset));
                ctx.Stroke();
            }
            else
            {
                ctx.NewPath();
            }

            ctx.Restore();
        }

        private static void DrawPanel(
            Context ctx,
            ElementBounds bounds,
            double fillR, double fillG, double fillB, double fillA,
            double borderR, double borderG, double borderB, double borderA,
            double borderWidth)
        {
            ctx.Save();
            ctx.SetSourceRGBA(fillR, fillG, fillB, fillA);
            ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
            ctx.Fill();

            if (borderWidth > 0 && borderA > 0)
            {
                ctx.SetSourceRGBA(borderR, borderG, borderB, borderA);
                ctx.LineWidth = borderWidth;
                double inset = borderWidth / 2.0;
                ctx.Rectangle(
                    bounds.drawX + inset,
                    bounds.drawY + inset,
                    Math.Max(0, bounds.InnerWidth - borderWidth),
                    Math.Max(0, bounds.InnerHeight - borderWidth));
                ctx.Stroke();
            }

            ctx.Restore();
        }

        private string? PrepareLogoTempFile(bool useServerLogo)
        {
            CleanupServerLogoTempFile();
            try
            {
                byte[]? bytes = null;
                if (useServerLogo)
                {
                    bytes = packet.ServerLogoPng;
                }
                else if (!string.IsNullOrWhiteSpace(packet.LogoAsset))
                {
                    var asset = capi.Assets.TryGet(new AssetLocation(packet.LogoAsset), true);
                    bytes = asset?.Data;
                }

                if (bytes == null || bytes.Length == 0) return null;

                serverLogoTempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "newfies-motd-logo-" + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(serverLogoTempPath, bytes);
                return serverLogoTempPath;
            }
            catch
            {
                serverLogoTempPath = null;
                return null;
            }
        }

        private bool LogoExists(string asset)
        {
            try { return capi.Assets.Exists(new AssetLocation(asset)); }
            catch { return false; }
        }

        private bool HasAnyTabs() => packet.EnableAnnouncementsTab || packet.EnableUpdatesTab || packet.EnableEventsTab || packet.EnableRulesTab || packet.EnableServerTab || packet.EnableCommandsTab;

        private string GetActiveVtml() => NormalizeVtml(activeTab switch
        {
            "news" => packet.AnnouncementsVtml,
            "updates" => packet.UpdatesVtml,
            "events" => packet.EventsVtml,
            "rules" => packet.RulesVtml,
            "server" => packet.ServerVtml,
            "commands" => packet.CommandsVtml,
            _ => packet.ContentVtml
        } ?? "");

        private void SwitchTab(string tab)
        {
            if (activeTab == tab) return;
            activeTab = tab;
            if (tab == "news") { onFeedOpenedCb(tab, packet); unreadAnnouncements = 0; packet.AnnouncementsVtml = StripNewBadges(packet.AnnouncementsVtml); }
            if (tab == "updates") { onFeedOpenedCb(tab, packet); unreadUpdates = 0; packet.UpdatesVtml = StripNewBadges(packet.UpdatesVtml); }
            // Recompose so the Server Info tab can switch to/from its dedicated two-column layout
            // and so active/unread tab identity is always refreshed.
            ComposeDialog();
        }

        private static string StripNewBadges(string? vtml) => Regex.Replace(vtml ?? "", @"<font color=""#FFC857""><strong>NEW</strong></font>\s*", "");

        public void ApplyRefresh(MotdPacket refreshed, int unreadNews, int unreadUpdateCount)
        {
            if (refreshed == null) return;

            // A live refresh recomposes the dialog because several pieces of content can change
            // (status strip, feeds, Server Info, etc.). Preserve both active scroll positions so
            // somebody reading halfway down a page is not thrown back to the top every refresh.
            float savedScrollY = SingleComposer?.GetScrollbar("vsb")?.CurrentYPosition ?? 0f;
            float savedScrollY2 = SingleComposer?.GetScrollbar("vsb2")?.CurrentYPosition ?? 0f;

            if ((refreshed.ServerLogoPng == null || refreshed.ServerLogoPng.Length == 0) && packet.ServerLogoPng != null && packet.ServerLogoPng.Length > 0 && string.Equals(refreshed.ServerLogoHash, packet.ServerLogoHash, StringComparison.OrdinalIgnoreCase))
                refreshed.ServerLogoPng = packet.ServerLogoPng;
            packet = refreshed;
            packet.ContentVtml = NormalizeVtml(packet.ContentVtml ?? "");
            unreadAnnouncements = activeTab == "news" ? 0 : unreadNews;
            unreadUpdates = activeTab == "updates" ? 0 : unreadUpdateCount;
            if (activeTab == "news") onFeedOpenedCb("news", packet);
            if (activeTab == "updates") onFeedOpenedCb("updates", packet);

            ComposeDialog();
            RestoreScroll(savedScrollY, savedScrollY2);
        }

        private void RestoreScroll(float scrollY, float scrollY2)
        {
            var vsb = SingleComposer?.GetScrollbar("vsb");
            if (vsb != null)
            {
                vsb.CurrentYPosition = Math.Max(0f, scrollY);
                OnScroll(vsb.CurrentYPosition);
            }

            var vsb2 = SingleComposer?.GetScrollbar("vsb2");
            if (vsb2 != null)
            {
                vsb2.CurrentYPosition = Math.Max(0f, scrollY2);
                OnScroll2(vsb2.CurrentYPosition);
            }
        }

        private void ResetScroll()
        {
            var text = SingleComposer?.GetRichtext("text");
            var vsb = SingleComposer?.GetScrollbar("vsb");
            if (text == null || vsb == null || textBounds == null) return;
            baseTextY = (float)text.Bounds.fixedY;
            float guiScale = Math.Max(0.01f, (float)RuntimeEnv.GUIScale);
            float logicalViewH = scrollViewportHeight > 0 ? scrollViewportHeight : (float)textBounds.fixedHeight;
            float viewH = Math.Max(1, (logicalViewH + packet.ScrollExtraPixels) * guiScale);
            float contentH = Math.Max(viewH, (float)text.TotalHeight + 24f * guiScale);
            vsb.SetHeights(viewH, contentH);
            vsb.CurrentYPosition = 0f;
            OnScroll(0f);

            var text2 = SingleComposer?.GetRichtext("text2");
            var vsb2 = SingleComposer?.GetScrollbar("vsb2");
            if (text2 != null && vsb2 != null && textBounds2 != null)
            {
                baseTextY2 = (float)text2.Bounds.fixedY;
                float guiScale2 = Math.Max(0.01f, (float)RuntimeEnv.GUIScale);
                float logicalViewH2 = scrollViewportHeight2 > 0 ? scrollViewportHeight2 : scrollViewportHeight;
                float viewH2 = Math.Max(1, (logicalViewH2 + packet.ScrollExtraPixels) * guiScale2);
                float contentH2 = Math.Max(viewH2, (float)text2.TotalHeight + 24f * guiScale2);
                vsb2.SetHeights(viewH2, contentH2);
                vsb2.CurrentYPosition = 0f;
            }
        }

        private void OnDontShowAgainToggled(bool value) => isCheckboxChecked = value;
        private bool ToggleDontShowAgainFromLabel()
        {
            isCheckboxChecked = !isCheckboxChecked;
            var checkbox = SingleComposer?.GetSwitch("dontshowagain");
            if (checkbox != null) checkbox.On = isCheckboxChecked;
            return true;
        }
        private bool OnOk() { TryClose(); return true; }
        private bool OnWebMapClicked()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(packet.WebMapUrl))
                    Process.Start(new ProcessStartInfo { FileName = packet.WebMapUrl, UseShellExecute = true });
            }
            catch (Exception ex) { capi.Logger.Warning("[MOTD] Failed to open Web Map '{0}': {1}", packet.WebMapUrl, ex.Message); }
            return true;
        }

        private bool OnLinkClicked()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(packet.LinkUrl))
                    Process.Start(new ProcessStartInfo { FileName = packet.LinkUrl, UseShellExecute = true });
            }
            catch (Exception ex) { capi.Logger.Warning("[MOTD] Failed to open link '{0}': {1}", packet.LinkUrl, ex.Message); }
            return true;
        }
        private void OnScroll(float value)
        {
            var text = SingleComposer?.GetRichtext("text");
            if (text == null) return;

            float guiScale = Math.Max(0.01f, (float)RuntimeEnv.GUIScale);
            float logicalOffset = value / guiScale;

            text.Bounds.fixedY = baseTextY - logicalOffset;
            text.Bounds.CalcWorldBounds();
        }
        private void OnScroll2(float value)
        {
            var text = SingleComposer?.GetRichtext("text2");
            if (text == null) return;

            float guiScale = Math.Max(0.01f, (float)RuntimeEnv.GUIScale);
            float logicalOffset = value / guiScale;
            text.Bounds.fixedY = baseTextY2 - logicalOffset;
            text.Bounds.CalcWorldBounds();
        }
        private bool OnCloseButton() { TryClose(); return true; }
        public override bool PrefersUngrabbedMouse => false;
        public override bool DisableMouseGrab => true;
    }
}
