using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace NewfiesCommunityMOTD;

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

	public override bool PrefersUngrabbedMouse => false;

	public override bool DisableMouseGrab => true;

	public GuiDialogMotd(ICoreClientAPI capi, MotdPacket packet, Action<bool> onClosed, Action<string, MotdPacket> onFeedOpened, int unreadAnnouncements, int unreadUpdates)
		: base(capi)
	{
		this.packet = packet;
		packet.ContentVtml = NormalizeVtml(packet.ContentVtml ?? "");
		onClosedCb = onClosed ?? ((Action<bool>)((bool _) =>
		{
		}));
		onFeedOpenedCb = onFeedOpened ?? ((Action<string, MotdPacket>)((string _, MotdPacket __) =>
		{
		}));
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
		if (!string.IsNullOrWhiteSpace(serverLogoTempPath))
		{
			try
			{
				File.Delete(serverLogoTempPath);
			}
			catch
			{
			}
			serverLogoTempPath = null;
		}
	}

	private static string NormalizeVtml(string s) => MotdFeedFormatter.Normalize(s);

	private void ComposeDialog()
	{
		ClearComposers();
		CleanupServerLogoTempFile();
		double num = Math.Max(0.5, RuntimeEnv.GUIScale);
		int max = Math.Max(620, (int)Math.Floor((double)capi.Render.FrameWidth / num) - 72);
		int max2 = Math.Max(480, (int)Math.Floor((double)capi.Render.FrameHeight / num) - 112);
		int desiredWidth = GameMath.Clamp(Math.Max(packet.Width, 760), 620, max);
		int num2 = GameMath.Clamp(Math.Max(packet.Height, 620), 520, max2);
		int num3 = GameMath.Clamp((packet.ScrollBarWidth <= 0) ? 18 : packet.ScrollBarWidth, 10, 30);
		bool flag = packet.EnableLogo && string.Equals(packet.LogoSource, "ServerFile", StringComparison.OrdinalIgnoreCase) && packet.ServerLogoPng != null && packet.ServerLogoPng.Length != 0;
		bool flag2 = packet.EnableLogo && !flag && !string.IsNullOrWhiteSpace(packet.LogoAsset) && LogoExists(packet.LogoAsset);
		bool flag3 = flag | flag2;
		int num4 = (flag3 ? GameMath.Clamp(packet.LogoHeight, 0, 400) : 0);
		int tabW = ((desiredWidth < 720) ? 100 : 112);
		int tabGap = ((desiredWidth < 650) ? 5 : 7);
		int num5 = 1 + (packet.EnableAnnouncementsTab ? 1 : 0) + (packet.EnableUpdatesTab ? 1 : 0) + (packet.EnableEventsTab ? 1 : 0) + (packet.EnableRulesTab ? 1 : 0) + (packet.EnableServerTab ? 1 : 0) + (packet.EnableCommandsTab ? 1 : 0);
		int num6 = Math.Max(1, (desiredWidth - 48 + tabGap) / (tabW + tabGap));
		int num7 = (HasAnyTabs() ? ((int)Math.Ceiling((double)num5 / (double)num6)) : 0);
		int num8 = num7 * 34;
		int num9 = 0;
		int num10 = (HasAnyTabs() ? 38 : 0);
		int num11 = 96 + num9 + num8 + num10 + 12;
		int num12 = (packet.EnableDontShowAgain ? 122 : 92);
		int num13 = num2 - (num11 + num12);
		int val = ((packet.ScrollOverrideHeightPx > 0) ? Math.Min(packet.ScrollOverrideHeightPx, num13) : num13);
		val = Math.Max(120, Math.Min(val, num2 - num11 - num12));
		int num14 = desiredWidth - 48 - num3 - 8;
		ElementBounds bounds = ElementBounds.Fixed(0.0, 0.0, desiredWidth, num2);
		string text = (string.IsNullOrWhiteSpace(packet.Title) ? "Message of the Day" : packet.Title);
		CairoFont cairoFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Left)
			.WithColor(new double[4] { 0.941, 0.82, 0.541, 1.0 });
		cairoFont.UnscaledFontsize = Math.Max(cairoFont.UnscaledFontsize, 22.0);
		CairoFont font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Left).WithColor(new double[4] { 0.57, 0.66, 0.71, 1.0 });
		CairoFont cairoFont2 = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Right)
			.WithColor(new double[4] { 0.494, 0.906, 0.529, 1.0 });
		cairoFont2.UnscaledFontsize = Math.Max(cairoFont2.UnscaledFontsize, 15.0);
		CairoFont font2 = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right).WithColor(new double[4] { 0.57, 0.66, 0.71, 1.0 });
		CairoFont tabFont = CairoFont.SmallButtonText(EnumButtonStyle.None).WithColor(new double[4] { 0.765, 0.816, 0.839, 1.0 });
		CairoFont tabActiveFont = CairoFont.SmallButtonText(EnumButtonStyle.None).WithWeight(FontWeight.Bold).WithColor(new double[4] { 1.0, 1.0, 1.0, 1.0 });
		CairoFont buttonFont = CairoFont.SmallButtonText(EnumButtonStyle.None).WithWeight(FontWeight.Bold).WithColor(new double[4] { 0.94, 0.82, 0.82, 1.0 });
		CairoFont buttonFont2 = CairoFont.SmallButtonText(EnumButtonStyle.None).WithWeight(FontWeight.Bold).WithColor(new double[4] { 0.87, 0.91, 0.93, 1.0 });
		CairoFont buttonFont3 = CairoFont.SmallButtonText(EnumButtonStyle.None).WithWeight(FontWeight.Bold).WithColor(new double[4] { 0.86, 0.88, 1.0, 1.0 });
		CairoFont buttonFont4 = CairoFont.SmallButtonText(EnumButtonStyle.None).WithWeight(FontWeight.Bold).WithColor(new double[4] { 0.95, 0.82, 0.55, 1.0 });
		double num15 = 180.0;
		double num16 = (double)(desiredWidth - 24 - 80) - num15;
		double num17 = 38.0;
		double fixedWidth = Math.Max(220.0, num16 - num17 - 16.0);
		GuiComposer compo = capi.Gui.CreateCompo("motd-dialog", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle)).BeginChildElements(bounds).AddStaticCustomDraw(ElementBounds.Fixed(6.0, 6.0, desiredWidth - 12, num2 - 12), (Context ctx, ImageSurface _, ElementBounds b) =>
		{
			DrawRoundedPanel(ctx, b, 14.0, 0.067, 0.094, 0.125, 0.985, 0.196, 0.267, 0.322, 1.0, 1.2);
		})
			.AddStaticCustomDraw(ElementBounds.Fixed(20.0, 14.0, desiredWidth - 40, 68.0), (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 10.0, 0.106, 0.161, 0.184, 0.985, 0.231, 0.314, 0.357, 1.0, 1.0, 0.788, 0.604, 0.282, 1.0, 2.0, accentAtBottom: true);
			})
			.AddStaticText(text, cairoFont, ElementBounds.Fixed(num17, 24.0, fixedWidth, 28.0), "dialogtitle")
			.AddStaticText(string.IsNullOrWhiteSpace(packet.Subtitle) ? "" : packet.Subtitle, font, ElementBounds.Fixed(num17, 51.0, fixedWidth, 18.0), "subtitle")
			.AddStaticCustomDraw(ElementBounds.Fixed(num16 - 10.0, 20.0, num15 + 22.0, 50.0), (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 7.0, 0.114, 0.169, 0.133, 0.96, 0.349, 0.596, 0.4, 1.0, 1.0);
			})
			.AddStaticText("● SERVER ONLINE", cairoFont2, ElementBounds.Fixed(num16, 27.0, num15, 18.0), "serveronline")
			.AddStaticText($"{packet.OnlineCount} / {packet.MaxPlayers} Players", font2, ElementBounds.Fixed(num16, 50.0, num15, 16.0), "headerplayers")
			.AddStaticCustomDraw(ElementBounds.Fixed(desiredWidth - 24 - 38, 24.0, 30.0, 28.0), (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 7.0, 0.165, 0.102, 0.102, 0.96, 0.486, 0.29, 0.29, 1.0, 1.0);
			})
			.AddButton("X", OnCloseButton, ElementBounds.Fixed(desiredWidth - 24 - 38, 24.0, 30.0, 28.0), buttonFont, EnumButtonStyle.None, "closebtn");
		double num18 = 96.0;
		int rowCapacity;
		int remainingTabs;
		int remainingRows;
		int currentRowCount;
		double tx;
		double ty;
		int tabIndexInRow;
		if (HasAnyTabs())
		{
			compo.AddStaticCustomDraw(ElementBounds.Fixed(19.0, num18 - 4.0, desiredWidth - 38, num8 + 10), (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 8.0, 0.067, 0.106, 0.129, 0.985, 0.2, 0.275, 0.31, 1.0, 1.0);
			});
			rowCapacity = Math.Max(1, num6);
			remainingTabs = num5;
			remainingRows = Math.Max(1, num7);
			currentRowCount = Math.Min(rowCapacity, (int)Math.Ceiling((double)remainingTabs / (double)remainingRows));
			tx = Math.Max(24.0, (double)(desiredWidth - (currentRowCount * tabW + Math.Max(0, currentRowCount - 1) * tabGap)) / 2.0);
			ty = num18;
			tabIndexInRow = 0;
			Tab("home", "Home", enabled: true);
			Tab("news", (unreadAnnouncements > 0) ? $"News ({unreadAnnouncements})" : "News", packet.EnableAnnouncementsTab);
			Tab("updates", (unreadUpdates > 0) ? $"Updates ({unreadUpdates})" : "Updates", packet.EnableUpdatesTab);
			Tab("events", "Events", packet.EnableEventsTab);
			Tab("rules", "Rules", packet.EnableRulesTab);
			Tab("server", "Server Info", packet.EnableServerTab);
			Tab("commands", "Commands", packet.EnableCommandsTab);
			num18 += (double)num8;
			string value = (string.IsNullOrWhiteSpace(packet.InGameDate) ? "Unavailable" : packet.InGameDate);
			string value2 = (string.IsNullOrWhiteSpace(packet.InGameTime) ? "" : (" · " + packet.InGameTime));
			string value3 = (string.IsNullOrWhiteSpace(packet.Season) ? "" : ("   |   " + packet.Season));
			string text2 = $"{value}{value2}   |   {packet.OnlineCount} / {packet.MaxPlayers} Players{value3}";
			CairoFont font3 = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
			num18 += 4.0;
			compo.AddStaticCustomDraw(ElementBounds.Fixed(24.0, num18 + 1.0, desiredWidth - 48, 30.0), (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 8.0, 0.063, 0.145, 0.212, 0.98, 0.361, 0.659, 0.839, 1.0, 1.0);
			});
			compo.AddStaticText(text2, font3, ElementBounds.Fixed(36.0, num18 + 8.0, desiredWidth - 48 - 24, 18.0), "infostrip");
			num18 += (double)num10;
		}
		num18 += 8.0;
		ElementBounds elementBounds = ElementBounds.Fixed(24.0, num18, num14, val);
		ElementBounds elementBounds2 = elementBounds.ForkContainingChild(12.0, 12.0, 12.0, 12.0);
		bool flag4 = (activeTab == "home") & flag3;
		int num19 = GameMath.Clamp(packet.LogoWidth, 0, 400);
		int num20 = GameMath.Clamp(packet.LogoHeight, 0, 400);
		int num21 = (flag4 ? ((num19 == 0) ? 400 : num19) : 0);
		int num22 = (flag4 ? ((num20 == 0) ? 400 : num20) : 0);
		if ((flag4 & flag) && packet.LogoPreserveAspectRatio)
		{
			try
			{
				byte[] serverLogoPng = packet.ServerLogoPng;
				int val2 = ((serverLogoPng.Length < 24) ? 1 : ((serverLogoPng[16] << 24) | (serverLogoPng[17] << 16) | (serverLogoPng[18] << 8) | serverLogoPng[19]));
				int val3 = ((serverLogoPng.Length < 24) ? 1 : ((serverLogoPng[20] << 24) | (serverLogoPng[21] << 16) | (serverLogoPng[22] << 8) | serverLogoPng[23]));
				double num23 = (double)Math.Max(1, val2) / (double)Math.Max(1, val3);
				if (num19 == 0 && num20 > 0)
				{
					num21 = Math.Min(400, Math.Max(1, (int)Math.Round((double)num22 * num23)));
				}
				else if (num20 == 0 && num19 > 0)
				{
					num22 = Math.Min(400, Math.Max(1, (int)Math.Round((double)num21 / num23)));
				}
				else if (num19 == 0 && num20 == 0)
				{
					if (num23 >= 1.0)
					{
						num21 = 400;
						num22 = Math.Max(1, (int)Math.Round(400.0 / num23));
					}
					else
					{
						num22 = 400;
						num21 = Math.Max(1, (int)Math.Round(400.0 * num23));
					}
				}
			}
			catch
			{
			}
		}
		int gap = (flag4 ? 12 : 0);
		scrollViewportHeight = (float)Math.Max(40.0, elementBounds2.fixedHeight - 24.0);
		textBounds = ElementBounds.Fixed(elementBounds2.fixedX + 12.0, elementBounds2.fixedY + 12.0, elementBounds2.fixedWidth - 24.0, scrollViewportHeight);
		ElementBounds bounds2 = ElementBounds.Fixed(24 + num14 + 5, num18 + 2.0, num3, val - 4);
		textFont = CairoFont.WhiteSmallText();
		if (activeTab == "server" && packet.EnableServerTab)
		{
			double num24 = 16.0;
			double num25 = ((double)(desiredWidth - 48) - num24) / 2.0;
			double fixedWidth2 = num25 - (double)num3 - 36.0;
			ElementBounds elementBounds3 = ElementBounds.Fixed(24.0, num18, num25, val);
			ElementBounds elementBounds4 = ElementBounds.Fixed(24.0 + num25 + num24, num18, num25, val);
			ElementBounds elementBounds5 = elementBounds3.ForkContainingChild(12.0, 12.0, 12.0, 12.0);
			ElementBounds elementBounds6 = elementBounds4.ForkContainingChild(12.0, 12.0, 12.0, 12.0);
			textBounds = ElementBounds.Fixed(elementBounds5.fixedX + 12.0, elementBounds5.fixedY + 12.0, fixedWidth2, elementBounds5.fixedHeight - 24.0);
			textBounds2 = ElementBounds.Fixed(elementBounds6.fixedX + 12.0, elementBounds6.fixedY + 12.0, fixedWidth2, elementBounds6.fixedHeight - 24.0);
			scrollViewportHeight = (float)Math.Max(40.0, elementBounds5.fixedHeight - 24.0);
			scrollViewportHeight2 = (float)Math.Max(40.0, elementBounds6.fixedHeight - 24.0);
			compo.AddStaticCustomDraw(elementBounds3, (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 9.0, 0.055, 0.082, 0.102, 0.98, 0.263, 0.349, 0.392, 1.0, 1.0, 0.361, 0.659, 0.839, 1.0, 3.0);
			});
			compo.BeginClip(elementBounds5);
			compo.AddRichtext(packet.ServerInfoLeftVtml, textFont, textBounds, "text");
			compo.EndClip();
			compo.AddVerticalScrollbar(OnScroll, ElementBounds.Fixed(24.0 + num25 - (double)num3 - 5.0, num18 + 2.0, num3, val - 4), "vsb");
			compo.AddStaticCustomDraw(elementBounds4, (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 9.0, 0.055, 0.082, 0.102, 0.98, 0.263, 0.349, 0.392, 1.0, 1.0, 0.788, 0.604, 0.282, 1.0, 3.0);
			});
			compo.BeginClip(elementBounds6);
			compo.AddRichtext(packet.ServerInfoRightVtml, textFont, textBounds2, "text2");
			compo.EndClip();
			compo.AddVerticalScrollbar(OnScroll2, ElementBounds.Fixed(24.0 + num25 + num24 + num25 - (double)num3 - 5.0, num18 + 2.0, num3, val - 4), "vsb2");
		}
		else
		{
			textBounds2 = null;
			scrollViewportHeight2 = 0f;
			compo.AddStaticCustomDraw(elementBounds, (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 10.0, 0.047, 0.071, 0.086, 0.98, 0.263, 0.349, 0.392, 1.0, 1.0, GetAccentForTab(activeTab).Item1, GetAccentForTab(activeTab).Item2, GetAccentForTab(activeTab).Item3, 1.0, 3.0);
			});
			compo.BeginClip(elementBounds2);
			string text3 = GetActiveVtml();
			if (activeTab == "home")
			{
				text3 = Regex.Replace(text3 ?? "", "\\{\\{serverlogo\\}\\}", "", RegexOptions.IgnoreCase);
			}
			if (flag4)
			{
				string text4 = PrepareLogoTempFile(flag);
				if (!string.IsNullOrWhiteSpace(text4))
				{
					RichTextComponentBase[] array = VtmlUtil.Richtextify(capi, text3 ?? "", textFont);
					RichTextComponentBase[] array2 = new RichTextComponentBase[array.Length + 1];
					array2[0] = new MotdLogoTextComponent(capi, text4, num21, num22, gap, packet.LogoPreserveAspectRatio);
					Array.Copy(array, 0, array2, 1, array.Length);
					compo.AddRichtext(array2, textBounds, "text");
				}
				else
				{
					compo.AddRichtext(text3, textFont, textBounds, "text");
				}
			}
			else
			{
				compo.AddRichtext(text3, textFont, textBounds, "text");
			}
			compo.EndClip();
			compo.AddVerticalScrollbar(OnScroll, bounds2, "vsb");
		}
		double num26 = num18 + (double)val;
		double fixedY = num26 + 7.0;
		double num27 = num26 + 16.0;
		double fixedHeight = 58.0;
		compo.AddStaticCustomDraw(ElementBounds.Fixed(24.0, fixedY, desiredWidth - 48, 2.0), (Context ctx, ImageSurface _, ElementBounds b) =>
		{
			DrawPanel(ctx, b, 0.788, 0.604, 0.282, 1.0, 0.788, 0.604, 0.282, 1.0, 0.0);
		});
		compo.AddStaticCustomDraw(ElementBounds.Fixed(19.0, num27, desiredWidth - 38, fixedHeight), (Context ctx, ImageSurface _, ElementBounds b) =>
		{
			DrawRoundedPanel(ctx, b, 8.0, 0.067, 0.106, 0.129, 0.98, 0.2, 0.275, 0.31, 1.0, 1.0);
		});
		double num28 = num27 + 16.0;
		double num29 = 90.0;
		double num30 = 154.0;
		double num31 = 14.0;
		double fixedX = (double)(desiredWidth - 24) - num29;
		if (packet.EnableDontShowAgain)
		{
			compo.AddSwitch(OnDontShowAgainToggled, ElementBounds.Fixed(24.0, num28 + 3.0, 32.0, 24.0), "dontshowagain", 22.0, 2.0);
			compo.AddStaticCustomDraw(ElementBounds.Fixed(62.0, num28 + 2.0, 180.0, 26.0), (Context ctx, ImageSurface _, ElementBounds b) =>
			{
				DrawRoundedPanel(ctx, b, 6.0, 0.075, 0.114, 0.137, 0.92, 0.204, 0.29, 0.333, 1.0, 1.0);
			});
			compo.AddButton(Lang.Get("Don't show again"), ToggleDontShowAgainFromLabel, ElementBounds.Fixed(62.0, num28 + 2.0, 180.0, 26.0), buttonFont2, EnumButtonStyle.None, "dontshowagainlabelbtn");
		}
		bool flag5 = packet.EnableLinkButton && !string.IsNullOrWhiteSpace(packet.LinkUrl);
		bool flag6 = packet.EnableWebMapButton && !string.IsNullOrWhiteSpace(packet.WebMapUrl);
		int num32 = (flag5 ? 1 : 0) + (flag6 ? 1 : 0);
		if (num32 > 0)
		{
			double num33 = (double)num32 * num30 + (double)Math.Max(0, num32 - 1) * num31;
			double num34 = ((double)desiredWidth - num33) / 2.0;
			if (flag5)
			{
				compo.AddStaticCustomDraw(ElementBounds.Fixed(num34, num28, num30, 30.0), (Context ctx, ImageSurface _, ElementBounds b) =>
				{
					DrawRoundedPanel(ctx, b, 6.0, 0.153, 0.192, 0.443, 0.96, 0.345, 0.396, 0.949, 1.0, 1.2);
				});
				compo.AddButton(string.IsNullOrWhiteSpace(packet.LinkButtonText) ? "Open Link" : packet.LinkButtonText, OnLinkClicked, ElementBounds.Fixed(num34, num28, num30, 30.0), buttonFont3, EnumButtonStyle.None, "linkbtn");
				num34 += num30 + num31;
			}
			if (flag6)
			{
				compo.AddStaticCustomDraw(ElementBounds.Fixed(num34, num28, num30, 30.0), (Context ctx, ImageSurface _, ElementBounds b) =>
				{
					DrawRoundedPanel(ctx, b, 6.0, 0.09, 0.196, 0.286, 0.96, 0.361, 0.659, 0.839, 1.0, 1.2);
				});
				compo.AddButton(string.IsNullOrWhiteSpace(packet.WebMapButtonText) ? "Web Map" : packet.WebMapButtonText, OnWebMapClicked, ElementBounds.Fixed(num34, num28, num30, 30.0), buttonFont2, EnumButtonStyle.None, "webmapbtn");
			}
		}
		compo.AddStaticCustomDraw(ElementBounds.Fixed(fixedX, num28, num29, 30.0), (Context ctx, ImageSurface _, ElementBounds b) =>
		{
			DrawRoundedPanel(ctx, b, 6.0, 0.231, 0.188, 0.125, 0.98, 0.788, 0.604, 0.282, 1.0, 1.2);
		});
		compo.AddButton(Lang.Get("Ok"), OnOk, ElementBounds.Fixed(fixedX, num28, num29, 30.0), buttonFont4, EnumButtonStyle.None, "okbtn");
		compo.EndChildElements();
		SingleComposer = compo.Compose();
		GuiElementSwitch guiElementSwitch = SingleComposer.GetSwitch("dontshowagain");
		if (guiElementSwitch != null)
		{
			guiElementSwitch.On = isCheckboxChecked;
		}
		ResetScroll();
		void Tab(string id, string label, bool enabled)
		{
			if (enabled)
			{
				if (tabIndexInRow >= currentRowCount)
				{
					remainingTabs -= currentRowCount;
					remainingRows = Math.Max(1, remainingRows - 1);
					currentRowCount = Math.Min(rowCapacity, (int)Math.Ceiling((double)remainingTabs / (double)remainingRows));
					tx = Math.Max(24.0, (double)(desiredWidth - (currentRowCount * tabW + Math.Max(0, currentRowCount - 1) * tabGap)) / 2.0);
					ty += 34.0;
					tabIndexInRow = 0;
				}
				bool flag7 = activeTab == id;
				ElementBounds bounds3 = ElementBounds.Fixed(tx, ty, tabW, 28.0);
				if (flag7)
				{
					compo.AddStaticCustomDraw(ElementBounds.Fixed(tx - 2.0, ty - 1.0, tabW + 4, 30.0), (Context ctx, ImageSurface _, ElementBounds b) =>
					{
						DrawRoundedPanel(ctx, b, 6.0, 0.157, 0.314, 0.416, 0.98, 0.361, 0.659, 0.839, 1.0, 1.2);
					});
				}
				else
				{
					compo.AddStaticCustomDraw(ElementBounds.Fixed(tx - 2.0, ty - 1.0, tabW + 4, 30.0), (Context ctx, ImageSurface _, ElementBounds b) =>
					{
						DrawRoundedPanel(ctx, b, 6.0, 0.09, 0.141, 0.173, 0.96, 0.204, 0.29, 0.333, 1.0, 1.0);
					});
				}
				compo.AddButton(label, () =>
				{
					SwitchTab(id);
					return true;
				}, bounds3, flag7 ? tabActiveFont : tabFont, EnumButtonStyle.None, "tab" + id);
				tx += tabW + tabGap;
				tabIndexInRow++;
			}
		}
	}

	public override void Dispose()
	{
		CleanupServerLogoTempFile();
		ClearComposers();
		base.Dispose();
	}

	private static (double, double, double) GetAccentForTab(string? tab)
	{
		string text = tab ?? "home";
		if (1 == 0)
		{
		}
		(double, double, double) result = text switch
		{
			"news" => (0.824, 0.643, 0.31), 
			"updates" => (0.475, 0.718, 0.357), 
			"events" => (0.361, 0.659, 0.839), 
			"rules" => (0.831, 0.416, 0.416), 
			"commands" => (0.839, 0.706, 0.361), 
			"server" => (0.361, 0.659, 0.839), 
			_ => (0.788, 0.604, 0.282), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static void RoundedRectPath(Context ctx, double x, double y, double width, double height, double radius)
	{
		radius = Math.Max(0.0, Math.Min(radius, Math.Min(width, height) / 2.0));
		if (radius <= 0.1)
		{
			ctx.Rectangle(x, y, width, height);
			return;
		}
		double num = x + width;
		double num2 = y + height;
		ctx.NewSubPath();
		ctx.Arc(num - radius, y + radius, radius, -Math.PI / 2.0, 0.0);
		ctx.Arc(num - radius, num2 - radius, radius, 0.0, Math.PI / 2.0);
		ctx.Arc(x + radius, num2 - radius, radius, Math.PI / 2.0, Math.PI);
		ctx.Arc(x + radius, y + radius, radius, Math.PI, 4.71238898038469);
		ctx.ClosePath();
	}

	private static void DrawRoundedPanel(Context ctx, ElementBounds bounds, double radius, double fillR, double fillG, double fillB, double fillA, double borderR, double borderG, double borderB, double borderA, double borderWidth, double accentR = 0.0, double accentG = 0.0, double accentB = 0.0, double accentA = 0.0, double accentSize = 0.0, bool accentAtBottom = false)
	{
		ctx.Save();
		double drawX = bounds.drawX;
		double drawY = bounds.drawY;
		double innerWidth = bounds.InnerWidth;
		double innerHeight = bounds.InnerHeight;
		if (innerWidth <= 0.0 || innerHeight <= 0.0)
		{
			ctx.Restore();
			return;
		}
		RoundedRectPath(ctx, drawX, drawY, innerWidth, innerHeight, radius);
		ctx.SetSourceRGBA(fillR, fillG, fillB, fillA);
		ctx.FillPreserve();
		if (accentA > 0.0 && accentSize > 0.0)
		{
			ctx.Save();
			ctx.Clip();
			ctx.SetSourceRGBA(accentR, accentG, accentB, accentA);
			if (accentAtBottom)
			{
				ctx.Rectangle(drawX, drawY + innerHeight - accentSize, innerWidth, accentSize);
			}
			else
			{
				ctx.Rectangle(drawX, drawY, innerWidth, accentSize);
			}
			ctx.Fill();
			ctx.Restore();
			RoundedRectPath(ctx, drawX, drawY, innerWidth, innerHeight, radius);
		}
		if (borderWidth > 0.0 && borderA > 0.0)
		{
			ctx.SetSourceRGBA(borderR, borderG, borderB, borderA);
			ctx.LineWidth = borderWidth;
			double num = borderWidth / 2.0;
			RoundedRectPath(ctx, drawX + num, drawY + num, Math.Max(0.0, innerWidth - borderWidth), Math.Max(0.0, innerHeight - borderWidth), Math.Max(0.0, radius - num));
			ctx.Stroke();
		}
		else
		{
			ctx.NewPath();
		}
		ctx.Restore();
	}

	private static void DrawPanel(Context ctx, ElementBounds bounds, double fillR, double fillG, double fillB, double fillA, double borderR, double borderG, double borderB, double borderA, double borderWidth)
	{
		ctx.Save();
		ctx.SetSourceRGBA(fillR, fillG, fillB, fillA);
		ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
		ctx.Fill();
		if (borderWidth > 0.0 && borderA > 0.0)
		{
			ctx.SetSourceRGBA(borderR, borderG, borderB, borderA);
			ctx.LineWidth = borderWidth;
			double num = borderWidth / 2.0;
			ctx.Rectangle(bounds.drawX + num, bounds.drawY + num, Math.Max(0.0, bounds.InnerWidth - borderWidth), Math.Max(0.0, bounds.InnerHeight - borderWidth));
			ctx.Stroke();
		}
		ctx.Restore();
	}

	private string? PrepareLogoTempFile(bool useServerLogo)
	{
		CleanupServerLogoTempFile();
		try
		{
			byte[] array = null;
			if (useServerLogo)
			{
				array = packet.ServerLogoPng;
			}
			else if (!string.IsNullOrWhiteSpace(packet.LogoAsset))
			{
				array = capi.Assets.TryGet(new AssetLocation(packet.LogoAsset))?.Data;
			}
			if (array == null || array.Length == 0)
			{
				return null;
			}
			serverLogoTempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "newfies-motd-logo-" + Guid.NewGuid().ToString("N") + ".png");
			File.WriteAllBytes(serverLogoTempPath, array);
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
		try
		{
			return capi.Assets.Exists(new AssetLocation(asset));
		}
		catch
		{
			return false;
		}
	}

	private bool HasAnyTabs()
	{
		return packet.EnableAnnouncementsTab || packet.EnableUpdatesTab || packet.EnableEventsTab || packet.EnableRulesTab || packet.EnableServerTab || packet.EnableCommandsTab;
	}

	private string GetActiveVtml()
	{
		string text = activeTab;
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"news" => packet.AnnouncementsVtml, 
			"updates" => packet.UpdatesVtml, 
			"events" => packet.EventsVtml, 
			"rules" => packet.RulesVtml, 
			"server" => packet.ServerVtml, 
			"commands" => packet.CommandsVtml, 
			_ => packet.ContentVtml, 
		};
		if (1 == 0)
		{
		}
		return NormalizeVtml(result ?? "");
	}

	private void SwitchTab(string tab)
	{
		if (!(activeTab == tab))
		{
			activeTab = tab;
			if (tab == "news")
			{
				onFeedOpenedCb(tab, packet);
				unreadAnnouncements = 0;
				packet.AnnouncementsVtml = StripNewBadges(packet.AnnouncementsVtml);
			}
			if (tab == "updates")
			{
				onFeedOpenedCb(tab, packet);
				unreadUpdates = 0;
				packet.UpdatesVtml = StripNewBadges(packet.UpdatesVtml);
			}
			ComposeDialog();
		}
	}

	private static string StripNewBadges(string? vtml)
	{
		return Regex.Replace(vtml ?? "", "<font color=\"#FFC857\"><strong>NEW</strong></font>\\s*", "");
	}

	public void ApplyRefresh(MotdPacket refreshed, int unreadNews, int unreadUpdateCount)
	{
		if (refreshed != null)
		{
			float valueOrDefault = (SingleComposer?.GetScrollbar("vsb")?.CurrentYPosition).GetValueOrDefault();
			float valueOrDefault2 = (SingleComposer?.GetScrollbar("vsb2")?.CurrentYPosition).GetValueOrDefault();
			if ((refreshed.ServerLogoPng == null || refreshed.ServerLogoPng.Length == 0) && packet.ServerLogoPng != null && packet.ServerLogoPng.Length != 0 && string.Equals(refreshed.ServerLogoHash, packet.ServerLogoHash, StringComparison.OrdinalIgnoreCase))
			{
				refreshed.ServerLogoPng = packet.ServerLogoPng;
			}
			packet = refreshed;
			packet.ContentVtml = NormalizeVtml(packet.ContentVtml ?? "");
			unreadAnnouncements = ((!(activeTab == "news")) ? unreadNews : 0);
			unreadUpdates = ((!(activeTab == "updates")) ? unreadUpdateCount : 0);
			if (activeTab == "news")
			{
				onFeedOpenedCb("news", packet);
			}
			if (activeTab == "updates")
			{
				onFeedOpenedCb("updates", packet);
			}
			ComposeDialog();
			RestoreScroll(valueOrDefault, valueOrDefault2);
		}
	}

	private void RestoreScroll(float scrollY, float scrollY2)
	{
		GuiElementScrollbar guiElementScrollbar = SingleComposer?.GetScrollbar("vsb");
		if (guiElementScrollbar != null)
		{
			guiElementScrollbar.CurrentYPosition = Math.Max(0f, scrollY);
			OnScroll(guiElementScrollbar.CurrentYPosition);
		}
		GuiElementScrollbar guiElementScrollbar2 = SingleComposer?.GetScrollbar("vsb2");
		if (guiElementScrollbar2 != null)
		{
			guiElementScrollbar2.CurrentYPosition = Math.Max(0f, scrollY2);
			OnScroll2(guiElementScrollbar2.CurrentYPosition);
		}
	}

	private void ResetScroll()
	{
		GuiElementRichtext guiElementRichtext = SingleComposer?.GetRichtext("text");
		GuiElementScrollbar guiElementScrollbar = SingleComposer?.GetScrollbar("vsb");
		if (guiElementRichtext != null && guiElementScrollbar != null && textBounds != null)
		{
			baseTextY = (float)guiElementRichtext.Bounds.fixedY;
			float num = Math.Max(0.01f, RuntimeEnv.GUIScale);
			float num2 = ((scrollViewportHeight > 0f) ? scrollViewportHeight : ((float)textBounds.fixedHeight));
			float num3 = Math.Max(1f, (num2 + (float)packet.ScrollExtraPixels) * num);
			float totalHeight = Math.Max(num3, (float)guiElementRichtext.TotalHeight + 24f * num);
			guiElementScrollbar.SetHeights(num3, totalHeight);
			guiElementScrollbar.CurrentYPosition = 0f;
			OnScroll(0f);
			GuiElementRichtext guiElementRichtext2 = SingleComposer?.GetRichtext("text2");
			GuiElementScrollbar guiElementScrollbar2 = SingleComposer?.GetScrollbar("vsb2");
			if (guiElementRichtext2 != null && guiElementScrollbar2 != null && textBounds2 != null)
			{
				baseTextY2 = (float)guiElementRichtext2.Bounds.fixedY;
				float num4 = Math.Max(0.01f, RuntimeEnv.GUIScale);
				float num5 = ((scrollViewportHeight2 > 0f) ? scrollViewportHeight2 : scrollViewportHeight);
				float num6 = Math.Max(1f, (num5 + (float)packet.ScrollExtraPixels) * num4);
				float totalHeight2 = Math.Max(num6, (float)guiElementRichtext2.TotalHeight + 24f * num4);
				guiElementScrollbar2.SetHeights(num6, totalHeight2);
				guiElementScrollbar2.CurrentYPosition = 0f;
			}
		}
	}

	private void OnDontShowAgainToggled(bool value)
	{
		isCheckboxChecked = value;
	}

	private bool ToggleDontShowAgainFromLabel()
	{
		isCheckboxChecked = !isCheckboxChecked;
		GuiElementSwitch guiElementSwitch = SingleComposer?.GetSwitch("dontshowagain");
		if (guiElementSwitch != null)
		{
			guiElementSwitch.On = isCheckboxChecked;
		}
		return true;
	}

	private bool OnOk()
	{
		TryClose();
		return true;
	}

	private bool OnWebMapClicked()
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(packet.WebMapUrl))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = packet.WebMapUrl,
					UseShellExecute = true
				});
			}
		}
		catch (Exception ex)
		{
			capi.Logger.Warning("[MOTD] Failed to open Web Map '{0}': {1}", packet.WebMapUrl, ex.Message);
		}
		return true;
	}

	private bool OnLinkClicked()
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(packet.LinkUrl))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = packet.LinkUrl,
					UseShellExecute = true
				});
			}
		}
		catch (Exception ex)
		{
			capi.Logger.Warning("[MOTD] Failed to open link '{0}': {1}", packet.LinkUrl, ex.Message);
		}
		return true;
	}

	private void OnScroll(float value)
	{
		GuiElementRichtext guiElementRichtext = SingleComposer?.GetRichtext("text");
		if (guiElementRichtext != null)
		{
			float num = Math.Max(0.01f, RuntimeEnv.GUIScale);
			float num2 = value / num;
			guiElementRichtext.Bounds.fixedY = baseTextY - num2;
			guiElementRichtext.Bounds.CalcWorldBounds();
		}
	}

	private void OnScroll2(float value)
	{
		GuiElementRichtext guiElementRichtext = SingleComposer?.GetRichtext("text2");
		if (guiElementRichtext != null)
		{
			float num = Math.Max(0.01f, RuntimeEnv.GUIScale);
			float num2 = value / num;
			guiElementRichtext.Bounds.fixedY = baseTextY2 - num2;
			guiElementRichtext.Bounds.CalcWorldBounds();
		}
	}

	private bool OnCloseButton()
	{
		TryClose();
		return true;
	}
}
