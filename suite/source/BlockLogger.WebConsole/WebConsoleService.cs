using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newfies.ChatProtocol;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.WebConsole;

public sealed class WebConsoleService : IDisposable
{
	private const int MaxReadBytes = 524288;

	private const long TailCacheFreshnessMilliseconds = 3000L;

	private readonly ICoreServerAPI api;

	private readonly Func<IServerPlayer, string>? displayName;

	private readonly Process process;

	private readonly object sync = new object();

	private DateTime lastCpuWall = DateTime.UtcNow;

	private TimeSpan lastCpuTime;

	private double cpuPercent;

	private double ramMb;

	private int playersOnline;

	private long inputEnabledUntil;

	private bool disposed;

	private long lastSampleUnixMilliseconds;

	private const long SampleFreshnessMilliseconds = 2000L;

	private string cachedTailPath = "";

	private long cachedTailLength = -1L;

	private long cachedTailUnixMilliseconds;

	private List<string> cachedTailLines = new List<string>();

	private readonly Queue<string> publicChat = new Queue<string>();

	private bool chatStarted;

	public WebConsoleService(ICoreServerAPI api, Func<IServerPlayer, string>? displayName = null)
	{
		this.api = api;
		this.displayName = displayName;
		process = Process.GetCurrentProcess();
		lastCpuTime = process.TotalProcessorTime;
	}

	public void Start()
	{
		Sample();
	}

	public void StartChatCapture()
	{
		if (!chatStarted && !disposed)
		{
			api.Event.PlayerChat += OnPlayerChat;
			chatStarted = true;
		}
	}

	private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
	{
		if (disposed || player == null || consumed.value || channelId != GlobalConstants.GeneralChatGroup || string.IsNullOrWhiteSpace(message))
		{
			return;
		}
		ChatMessageBoundary.Line line = ChatMessageBoundary.Parse(message, player.PlayerName, displayName?.Invoke(player), player.Role?.Name);
		if (line.IsCommand)
		{
			return;
		}
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("playerUid", player.PlayerUID);
		treeAttribute.SetInt("channelId", channelId);
		treeAttribute.SetString("rawLine", message);
		treeAttribute.SetString("body", line.Body);
		try
		{
			api.Event.PushEvent("newfiesse:chat-presentation-query", treeAttribute);
		}
		catch
		{
			return;
		}
		if (treeAttribute.GetBool("blocked"))
		{
			return;
		}
		string text = (treeAttribute.GetBool("available") ? treeAttribute.GetString("visibleText", line.Body) : WebUtility.HtmlDecode(line.Body));
		string text2 = text.Replace("\r", " ").Replace("\n", " ");
		if (text2.Length > 2000)
		{
			text2 = text2.Substring(0, 2000);
		}
		string value = (player.PlayerName ?? "Player").Replace("\r", " ").Replace("\n", " ");
		lock (sync)
		{
			publicChat.Enqueue($"{DateTime.UtcNow:HH:mm:ss} UTC [Game Chat] {value}: {text2}");
			while (publicChat.Count > 500)
			{
				publicChat.Dequeue();
			}
		}
	}

	public WebConsoleSnapshot GetSnapshot(int lines, bool includePlayerChat = false)
	{
		EnsureFreshSample();
		lines = Math.Clamp(lines, 50, 1000);
		string text = ResolveCurrentLogPath();
		List<string> tailCached = GetTailCached(text, lines, out var fileLength);
		lock (sync)
		{
			long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			bool flag = inputEnabledUntil > num;
			if (!flag)
			{
				inputEnabledUntil = 0L;
			}
			return new WebConsoleSnapshot
			{
				CommandInputEnabled = flag,
				CommandInputExpiresUnixMilliseconds = inputEnabledUntil,
				CpuPercent = cpuPercent,
				RamMb = ramMb,
				PlayersOnline = playersOnline,
				LogPath = text,
				LogLength = fileLength,
				Lines = tailCached,
				PlayerChatIncluded = includePlayerChat,
				ChatLines = (includePlayerChat ? publicChat.ToList() : new List<string>())
			};
		}
	}

	public bool SetInputEnabled(bool enabled)
	{
		lock (sync)
		{
			inputEnabledUntil = (enabled ? DateTimeOffset.UtcNow.AddMinutes(15.0).ToUnixTimeMilliseconds() : 0);
			return enabled;
		}
	}

	public bool IsInputEnabled()
	{
		lock (sync)
		{
			return inputEnabledUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
	}

	public void TouchInputLease()
	{
		lock (sync)
		{
			if (inputEnabledUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
			{
				inputEnabledUntil = DateTimeOffset.UtcNow.AddMinutes(15.0).ToUnixTimeMilliseconds();
			}
		}
	}

	private void EnsureFreshSample()
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (num - Interlocked.Read(in lastSampleUnixMilliseconds) >= 2000)
		{
			Sample();
		}
	}

	private void Sample()
	{
		if (disposed)
		{
			return;
		}
		try
		{
			DateTime utcNow = DateTime.UtcNow;
			TimeSpan totalProcessorTime = process.TotalProcessorTime;
			double num = Math.Max(0.001, (utcNow - lastCpuWall).TotalSeconds);
			double num2 = Math.Max(0.0, (totalProcessorTime - lastCpuTime).TotalSeconds);
			lastCpuWall = utcNow;
			lastCpuTime = totalProcessorTime;
			double num3 = Math.Clamp(num2 / num / (double)Math.Max(1, Environment.ProcessorCount) * 100.0, 0.0, 100.0);
			double num4 = (double)process.WorkingSet64 / 1048576.0;
			int num5 = 0;
			try
			{
				num5 = api.World.AllOnlinePlayers?.Length ?? 0;
			}
			catch
			{
			}
			lock (sync)
			{
				cpuPercent = num3;
				ramMb = num4;
				playersOnline = num5;
			}
			Interlocked.Exchange(ref lastSampleUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		}
		catch
		{
		}
	}

	private string ResolveCurrentLogPath()
	{
		string text;
		try
		{
			text = api.GetOrCreateDataPath("Logs");
		}
		catch
		{
			string orCreateDataPath = api.GetOrCreateDataPath("ModData");
			text = Path.Combine(Directory.GetParent(orCreateDataPath)?.FullName ?? orCreateDataPath, "Logs");
		}
		string text2 = Path.Combine(text, "server-main.log");
		if (File.Exists(text2))
		{
			return text2;
		}
		try
		{
			string text3 = null;
			DateTime dateTime = DateTime.MinValue;
			foreach (string item in Directory.EnumerateFiles(text, "server-main*.log", SearchOption.TopDirectoryOnly))
			{
				DateTime lastWriteTimeUtc;
				try
				{
					lastWriteTimeUtc = File.GetLastWriteTimeUtc(item);
				}
				catch
				{
					continue;
				}
				if (text3 == null || lastWriteTimeUtc > dateTime)
				{
					text3 = item;
					dateTime = lastWriteTimeUtc;
				}
			}
			return text3 ?? text2;
		}
		catch
		{
			return text2;
		}
	}

	private List<string> GetTailCached(string path, int maxLines, out long fileLength)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long val;
		try
		{
			val = (File.Exists(path) ? new FileInfo(path).Length : 0);
		}
		catch
		{
			val = -1L;
		}
		lock (sync)
		{
			if (string.Equals(cachedTailPath, path, StringComparison.Ordinal) && num - cachedTailUnixMilliseconds < 3000 && cachedTailLines.Count > 0)
			{
				fileLength = Math.Max(0L, val);
				return CopyTail(cachedTailLines, maxLines);
			}
		}
		List<string> list = Tail(path, 1000, out fileLength);
		lock (sync)
		{
			cachedTailPath = path;
			cachedTailLength = fileLength;
			cachedTailUnixMilliseconds = num;
			cachedTailLines = list;
			return CopyTail(cachedTailLines, maxLines);
		}
	}

	private static List<string> CopyTail(List<string> source, int maxLines)
	{
		int num = Math.Min(maxLines, source.Count);
		int num2 = source.Count - num;
		List<string> list = new List<string>(num);
		for (int i = num2; i < source.Count; i++)
		{
			list.Add(source[i]);
		}
		return list;
	}

	private static List<string> Tail(string path, int maxLines, out long fileLength)
	{
		fileLength = 0L;
		try
		{
			if (!File.Exists(path))
			{
				return new List<string> { "server-main.log is not available yet." };
			}
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			fileLength = fileStream.Length;
			long num = Math.Max(0L, fileStream.Length - 524288);
			fileStream.Seek(num, SeekOrigin.Begin);
			using StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true, 4096, false);
			string text = streamReader.ReadToEnd();
			if (num > 0)
			{
				int num2 = text.IndexOf('\n');
				if (num2 >= 0)
				{
					text = text.Substring(num2 + 1);
				}
			}
			string[] array = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
			int num3 = Math.Min(maxLines, array.Length);
			int num4 = array.Length - num3;
			List<string> list = new List<string>(num3);
			for (int i = num4; i < array.Length; i++)
			{
				list.Add(array[i]);
			}
			return list;
		}
		catch (Exception ex)
		{
			return new List<string> { "Console mirror read failed: " + ex.GetType().Name + ": " + ex.Message };
		}
	}

	public void Dispose()
	{
		disposed = true;
		if (chatStarted)
		{
			api.Event.PlayerChat -= OnPlayerChat;
			chatStarted = false;
		}
		lock (sync)
		{
			publicChat.Clear();
		}
		try
		{
			process.Dispose();
		}
		catch
		{
		}
	}
}
