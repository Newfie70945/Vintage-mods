using System;
using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public static class ActionCenterWorkflow
{
	public static int Clear(OperationsCenterStore store, string target, IReadOnlyCollection<string> ids)
	{
		HashSet<string> selected = new HashSet<string>(ids, StringComparer.Ordinal);
		if (target == "alerts")
		{
			List<ServerAlertRecord> list = store.Alerts.FindAll((ServerAlertRecord x) => selected.Contains(x.Id));
			store.Alerts.RemoveAll((ServerAlertRecord x) => selected.Contains(x.Id));
			HashSet<string> linked = new HashSet<string>(list.ConvertAll((ServerAlertRecord x) => "alert:" + x.Id), StringComparer.Ordinal);
			store.Actions.RemoveAll((ServerActionRecord x) => linked.Contains(x.Id));
			return list.Count;
		}
		if (target != "actions")
		{
			throw new ArgumentException("Unknown clear target.");
		}
		int num = 0;
		foreach (ServerActionRecord action in store.Actions)
		{
			if (!(action.Status == "Cleared") && selected.Contains(action.Id))
			{
				action.Status = "Cleared";
				num++;
			}
		}
		return num;
	}
}
