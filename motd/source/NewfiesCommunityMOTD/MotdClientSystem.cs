using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace NewfiesCommunityMOTD;

public class MotdClientSystem : ModSystem
{
	private ICoreClientAPI? capi;

	private MotdClientState? state;

	private readonly HashSet<string> seenThisSession = new HashSet<string>();

	private GuiDialogMotd? currentDialog;

	private MotdPacket? currentPacket;

	private bool disposed;

	private const string StatePath = "MotdClientState.json";

	private const int MIN_W = 700;

	private const int MAX_W = 1600;

	private const int MIN_H = 520;

	private const int MAX_H = 1000;

	public override bool ShouldLoad(EnumAppSide forSide)
	{
		return forSide == EnumAppSide.Client || forSide == EnumAppSide.Universal;
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		capi = api;
		LoadState();
		capi.Network.RegisterChannel("newfiescommunitymotd").RegisterMessageType(typeof(MotdPacket)).RegisterMessageType(typeof(MotdResetPacket))
			.SetMessageHandler<MotdPacket>(OnMotdPacket)
			.SetMessageHandler<MotdResetPacket>(OnMotdResetPacket);
		capi.Logger.Notification("[MOTD] Client started.");
	}

	private void OnMotdResetPacket(MotdResetPacket pkt)
	{
		if (pkt != null && pkt.ResetAll)
		{
			state = new MotdClientState();
			seenThisSession.Clear();
			SaveState();
			capi?.Logger.Notification("[MOTD] Client caches cleared by server.");
		}
	}

	private void OnMotdPacket(MotdPacket pkt)
	{
		if (pkt == null || disposed)
		{
			return;
		}
		DecorateUnreadCards(pkt);
		if (pkt.IsRefresh)
		{
			if (currentDialog != null && currentDialog.IsOpened())
			{
				currentPacket = pkt;
				currentDialog.ApplyRefresh(pkt, GetUnreadAnnouncementCount(pkt), GetUnreadUpdateCount(pkt));
			}
		}
		else
		{
			if (!ShouldShow(pkt, out string _))
			{
				return;
			}
			int num = GameMath.Clamp(pkt.Width, 700, 1600);
			int num2 = GameMath.Clamp(pkt.Height, 520, 1000);
			currentPacket = pkt;
			if (currentDialog == null)
			{
				currentDialog = new GuiDialogMotd(capi, pkt, (bool dontShowAgain) =>
				{
					if (!disposed && currentPacket != null)
					{
						MarkShown(currentPacket, dontShowAgain);
					}
				}, MarkFeedRead, GetUnreadAnnouncementCount(pkt), GetUnreadUpdateCount(pkt));
			}
			else
			{
				currentDialog.ApplyRefresh(pkt, GetUnreadAnnouncementCount(pkt), GetUnreadUpdateCount(pkt));
			}
			currentDialog.TryOpen();
		}
	}

	private void DecorateUnreadCards(MotdPacket p)
	{
		if (state == null)
		{
			state = new MotdClientState();
		}
		p.AnnouncementsVtml = DecorateFeed(p.AnnouncementsVtml, state.ReadAnnouncementIds);
		p.UpdatesVtml = DecorateFeed(p.UpdatesVtml, state.ReadUpdateIds);
	}

	private static string DecorateFeed(string? vtml, HashSet<string> readIds)
	{
		if (string.IsNullOrEmpty(vtml))
		{
			return vtml ?? "";
		}
		return Regex.Replace(vtml, "\\[\\[MOTDNEW:([^\\]]+)\\]\\]", (Match m) =>
		{
			string item = WebUtility.HtmlDecode(m.Groups[1].Value);
			return readIds.Contains(item) ? "" : "<font color=\"#FFC857\"><strong>NEW</strong></font>  ";
		});
	}

	private bool ShouldShow(MotdPacket p, out string reason)
	{
		reason = "ok";
		if (p.ForceShow)
		{
			return true;
		}
		string text = p.ContentHash ?? "";
		string key = p.VersionLabel ?? "";
		if (state != null && state.SeenEver.Contains(text))
		{
			reason = "SeenEver";
			return false;
		}
		if (p.OnlyOncePerSession && seenThisSession.Contains(text))
		{
			reason = "SeenThisSession";
			return false;
		}
		if (p.OnlyOncePerVersion && state != null && state.SeenByVersion.TryGetValue(key, out string value) && value == text)
		{
			reason = "SeenByVersion";
			return false;
		}
		return true;
	}

	private void MarkShown(MotdPacket p, bool dontShowAgain)
	{
		string text = p.ContentHash ?? "";
		string key = p.VersionLabel ?? "";
		seenThisSession.Add(text);
		if (state == null)
		{
			state = new MotdClientState();
		}
		if (p.OnlyOncePerVersion)
		{
			state.SeenByVersion[key] = text;
		}
		if (dontShowAgain)
		{
			state.SeenEver.Add(text);
		}
		SaveState();
	}

	private int GetUnreadAnnouncementCount(MotdPacket p)
	{
		if (state == null)
		{
			state = new MotdClientState();
		}
		return (p.AnnouncementIds ?? new List<string>()).Count((string id) => !state.ReadAnnouncementIds.Contains(id));
	}

	private int GetUnreadUpdateCount(MotdPacket p)
	{
		if (state == null)
		{
			state = new MotdClientState();
		}
		return (p.UpdateIds ?? new List<string>()).Count((string id) => !state.ReadUpdateIds.Contains(id));
	}

	private void MarkFeedRead(string tab, MotdPacket p)
	{
		if (state == null)
		{
			state = new MotdClientState();
		}
		if (tab == "news")
		{
			foreach (string item in p.AnnouncementIds ?? new List<string>())
			{
				state.ReadAnnouncementIds.Add(item);
			}
		}
		if (tab == "updates")
		{
			foreach (string item2 in p.UpdateIds ?? new List<string>())
			{
				state.ReadUpdateIds.Add(item2);
			}
		}
		SaveState();
	}

	private void LoadState()
	{
		state = capi?.LoadModConfig<MotdClientState>("MotdClientState.json") ?? new MotdClientState();
	}

	private void SaveState()
	{
		if (capi != null && state != null)
		{
			capi.StoreModConfig(state, "MotdClientState.json");
		}
	}

	public override void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			if (currentDialog != null)
			{
				currentDialog.TryClose();
				currentDialog.Dispose();
				capi?.Gui.LoadedGuis.Remove(currentDialog);
				currentDialog = null;
			}
			currentPacket = null;
			seenThisSession.Clear();
			state = null;
			capi = null;
			base.Dispose();
		}
	}
}
