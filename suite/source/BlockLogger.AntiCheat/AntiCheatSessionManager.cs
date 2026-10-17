using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatSessionManager
{
	private readonly ConcurrentDictionary<string, AntiCheatSession> sessions = new ConcurrentDictionary<string, AntiCheatSession>(StringComparer.OrdinalIgnoreCase);

	public int Count => sessions.Count;

	public AntiCheatSession GetOrCreate(string uid, string name)
	{
		AntiCheatSession orAdd = sessions.GetOrAdd(uid, (string _) => new AntiCheatSession
		{
			PlayerUid = uid,
			PlayerName = name
		});
		orAdd.PlayerName = name;
		return orAdd;
	}

	public bool TryGet(string uid, out AntiCheatSession session)
	{
		return sessions.TryGetValue(uid, out session);
	}

	public IReadOnlyList<AntiCheatSession> Snapshot()
	{
		return sessions.Values.OrderBy((AntiCheatSession x) => x.PlayerName).ToList();
	}

	public void Remove(string uid)
	{
		sessions.TryRemove(uid, out AntiCheatSession _);
	}

	public void Clear()
	{
		sessions.Clear();
	}
}
