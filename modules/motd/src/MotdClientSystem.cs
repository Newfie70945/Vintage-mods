using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace NewfiesCommunityMOTD
{
    class MotdClientState
    {
        public HashSet<string> SeenEver = new();
        public Dictionary<string, string> SeenByVersion = new();
        public HashSet<string> ReadAnnouncementIds = new();
        public HashSet<string> ReadUpdateIds = new();
    }

    public class MotdClientSystem : ModSystem
    {
        private ICoreClientAPI? capi;
        private MotdClientState? state;
        private readonly HashSet<string> seenThisSession = new();
        private GuiDialogMotd? currentDialog;
        private MotdPacket? currentPacket;
        private bool disposed;
        private const string StatePath = "MotdClientState.json";
        private const int MIN_W = 700, MAX_W = 1600, MIN_H = 520, MAX_H = 1000;

        public override bool ShouldLoad(EnumAppSide forSide) =>
            forSide == EnumAppSide.Client || forSide == EnumAppSide.Universal;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            LoadState();

            capi.Network.RegisterChannel("newfiescommunitymotd")
                .RegisterMessageType(typeof(MotdPacket))
                .RegisterMessageType(typeof(MotdResetPacket))
                .SetMessageHandler<MotdPacket>(OnMotdPacket)
                .SetMessageHandler<MotdResetPacket>(OnMotdResetPacket);

            capi.Logger.Notification("[MOTD] Client started.");
        }

        private void OnMotdResetPacket(MotdResetPacket pkt)
        {
            if (pkt?.ResetAll == true)
            {
                state = new MotdClientState();
                seenThisSession.Clear();
                SaveState();
                capi?.Logger.Notification("[MOTD] Client caches cleared by server.");
            }
        }

        private void OnMotdPacket(MotdPacket pkt)
        {
            if (pkt == null || disposed) return;

            // Decorate per-card unread markers locally. Read state is client-owned, so the server
            // never needs to know which announcements a particular player has opened.
            DecorateUnreadCards(pkt);

            // Refresh packets update an already-open dialog only. They never pop MOTD back open.
            if (pkt.IsRefresh)
            {
                if (currentDialog != null && currentDialog.IsOpened())
                {
                    currentPacket = pkt;
                    currentDialog.ApplyRefresh(pkt, GetUnreadAnnouncementCount(pkt), GetUnreadUpdateCount(pkt));
                }
                return;
            }

            if (!ShouldShow(pkt, out var reason))
            {
                return;
            }

            int w = GameMath.Clamp(pkt.Width, MIN_W, MAX_W);
            int h = GameMath.Clamp(pkt.Height, MIN_H, MAX_H);

            currentPacket = pkt;
            // TryClose does not unregister a GuiDialog from LoadedGuis. Reuse one
            // dialog instead of rooting another packet/callback/UI on every /motd.
            if (currentDialog == null) currentDialog = new GuiDialogMotd(
                capi!,
                pkt,
                onClosed: (dontShowAgain) => { if (!disposed && currentPacket != null) MarkShown(currentPacket, dontShowAgain); },
                onFeedOpened: MarkFeedRead,
                unreadAnnouncements: GetUnreadAnnouncementCount(pkt),
                unreadUpdates: GetUnreadUpdateCount(pkt)
            );
            else currentDialog.ApplyRefresh(pkt, GetUnreadAnnouncementCount(pkt), GetUnreadUpdateCount(pkt));
            currentDialog.TryOpen();
        }


        private void DecorateUnreadCards(MotdPacket p)
        {
            state ??= new MotdClientState();
            p.AnnouncementsVtml = DecorateFeed(p.AnnouncementsVtml, state.ReadAnnouncementIds);
            p.UpdatesVtml = DecorateFeed(p.UpdatesVtml, state.ReadUpdateIds);
        }

        private static string DecorateFeed(string? vtml, HashSet<string> readIds)
        {
            if (string.IsNullOrEmpty(vtml)) return vtml ?? "";
            return Regex.Replace(vtml, @"\[\[MOTDNEW:([^\]]+)\]\]", m =>
            {
                string id = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                return readIds.Contains(id) ? "" : "<font color=\"#FFC857\"><strong>NEW</strong></font>  ";
            });
        }

        private bool ShouldShow(MotdPacket p, out string reason)
        {
            reason = "ok";
            if (p.ForceShow) return true;
            string hash = p.ContentHash ?? "";
            string ver  = p.VersionLabel ?? "";
            if (state != null && state.SeenEver.Contains(hash))                                             { reason = "SeenEver"; return false; }
            if (p.OnlyOncePerSession && seenThisSession.Contains(hash))                                     { reason = "SeenThisSession"; return false; }
            if (p.OnlyOncePerVersion && state != null && state.SeenByVersion.TryGetValue(ver, out var seen) && seen == hash) { reason = "SeenByVersion"; return false; }
            return true;
        }

        private void MarkShown(MotdPacket p, bool dontShowAgain)
        {
            string hash = p.ContentHash ?? "";
            string ver  = p.VersionLabel ?? "";
            seenThisSession.Add(hash);
            if (state == null) state = new MotdClientState();
            if (p.OnlyOncePerVersion) state.SeenByVersion[ver] = hash;
            if (dontShowAgain) state.SeenEver.Add(hash);
            SaveState();
        }

        private int GetUnreadAnnouncementCount(MotdPacket p)
        {
            state ??= new MotdClientState();
            return (p.AnnouncementIds ?? new List<string>()).Count(id => !state.ReadAnnouncementIds.Contains(id));
        }

        private int GetUnreadUpdateCount(MotdPacket p)
        {
            state ??= new MotdClientState();
            return (p.UpdateIds ?? new List<string>()).Count(id => !state.ReadUpdateIds.Contains(id));
        }

        private void MarkFeedRead(string tab, MotdPacket p)
        {
            state ??= new MotdClientState();
            if (tab == "news") foreach (var id in p.AnnouncementIds ?? new List<string>()) state.ReadAnnouncementIds.Add(id);
            if (tab == "updates") foreach (var id in p.UpdateIds ?? new List<string>()) state.ReadUpdateIds.Add(id);
            SaveState();
        }

        private void LoadState() => state = capi?.LoadModConfig<MotdClientState>(StatePath) ?? new MotdClientState();
        private void SaveState() { if (capi != null && state != null) capi.StoreModConfig(state, StatePath); }

        public override void Dispose()
        {
            if (disposed) return;
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
