using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NewfiesCommunityMOTD;

internal sealed class DiscordRuntimeAdapter : INewfiesDiscordMotdProvider
{
	private object? Provider => (from a in AppDomain.CurrentDomain.GetAssemblies()
		select a.GetType("NewfiesServerEssentials.Discord.DiscordIntegrationModSystem", throwOnError: false)).FirstOrDefault((Type t) => t != null)?.GetProperty("CurrentMotdProvider", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

	public int ProtocolVersion => 1;

	public bool IsAvailable => ReadBool("IsAvailable");

	public event Action? ContentChanged
	{
		add
		{
		}
		remove
		{
		}
	}

	public IReadOnlyList<MotdFeedItem> GetAnnouncements()
	{
		return ReadFeed("GetAnnouncements");
	}

	public IReadOnlyList<MotdFeedItem> GetUpdates()
	{
		return ReadFeed("GetUpdates");
	}

	public IReadOnlyList<MotdFeedItem> GetEvents()
	{
		return ReadFeed("GetEvents");
	}

	private bool ReadBool(string property)
	{
		try
		{
			return Provider?.GetType().GetProperty(property)?.GetValue(Provider) as bool? == true;
		}
		catch
		{
			return false;
		}
	}

	private IReadOnlyList<MotdFeedItem> ReadFeed(string method)
	{
		try
		{
			object provider = Provider;
			if (!(provider?.GetType().GetMethod(method, Type.EmptyTypes)?.Invoke(provider, null) is IEnumerable enumerable))
			{
				return Array.Empty<MotdFeedItem>();
			}
			List<MotdFeedItem> list = new List<MotdFeedItem>();
			foreach (object item2 in enumerable)
			{
				object item = item2;
				Type t;
				if (item != null)
				{
					t = item.GetType();
					list.Add(new MotdFeedItem
					{
						Id = (Get("Id")?.ToString() ?? ""),
						Title = (Get("Title")?.ToString() ?? ""),
						BodyVtml = (Get("BodyVtml")?.ToString() ?? ""),
						Author = (Get("Author")?.ToString() ?? ""),
						UnixTime = Convert.ToInt64(Get("UnixTime") ?? ((object)0L)),
						Url = (Get("Url")?.ToString() ?? ""),
						Important = Convert.ToBoolean(Get("Important") ?? ((object)false))
					});
				}
				object? Get(string n)
				{
					return t.GetProperty(n)?.GetValue(item);
				}
			}
			return list;
		}
		catch
		{
			return Array.Empty<MotdFeedItem>();
		}
	}
}
