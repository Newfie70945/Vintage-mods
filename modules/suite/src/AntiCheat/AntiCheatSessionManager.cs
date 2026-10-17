using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatSessionManager
{
    private readonly ConcurrentDictionary<string, AntiCheatSession> sessions = new(System.StringComparer.OrdinalIgnoreCase);

    public int Count => sessions.Count;

    public AntiCheatSession GetOrCreate(string uid, string name)
    {
        var session = sessions.GetOrAdd(uid, _ => new AntiCheatSession { PlayerUid = uid, PlayerName = name });
        session.PlayerName = name;
        return session;
    }

    public bool TryGet(string uid, out AntiCheatSession session) => sessions.TryGetValue(uid, out session!);
    public IReadOnlyList<AntiCheatSession> Snapshot() => sessions.Values.OrderBy(x => x.PlayerName).ToList();
    public void Remove(string uid) => sessions.TryRemove(uid, out _);
    public void Clear() => sessions.Clear();
}
