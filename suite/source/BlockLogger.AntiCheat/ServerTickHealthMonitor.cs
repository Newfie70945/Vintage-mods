using System;
using System.Diagnostics;
using Vintagestory.API.Server;

namespace BlockLogger.AntiCheat;

internal sealed class ServerTickHealthMonitor : IDisposable
{
	private readonly ICoreServerAPI api;

	private readonly AntiCheatConfig config;

	private readonly object sync = new object();

	private long listenerId;

	private long lastStamp;

	private double targetTickMs = 50.0;

	private double targetTps = 20.0;

	private double smoothedTickMs = 50.0;

	private int sampleCount;

	private bool started;

	public ServerTickHealthMonitor(ICoreServerAPI api, AntiCheatConfig config)
	{
		this.api = api;
		this.config = config;
	}

	public void Start()
	{
		if (started)
		{
			return;
		}
		started = true;
		try
		{
			double num = api.Server.Config.TickTime;
			if (double.IsFinite(num) && num >= 5.0 && num <= 1000.0)
			{
				targetTickMs = num;
			}
		}
		catch
		{
			targetTickMs = 50.0;
		}
		targetTps = 1000.0 / Math.Max(1.0, targetTickMs);
		smoothedTickMs = targetTickMs;
		lastStamp = 0L;
		sampleCount = 0;
		int num2 = Math.Clamp((int)Math.Round(targetTickMs / 2.0), 5, 250);
		listenerId = api.Event.RegisterGameTickListener(OnTick, num2, num2);
	}

	private void OnTick(float dt)
	{
		long timestamp = Stopwatch.GetTimestamp();
		lock (sync)
		{
			if (lastStamp == 0)
			{
				lastStamp = timestamp;
				return;
			}
			double num = (double)(timestamp - lastStamp) * 1000.0 / (double)Stopwatch.Frequency;
			lastStamp = timestamp;
			if (double.IsFinite(num) && !(num <= 0.0) && !(num < targetTickMs * 0.5))
			{
				double max = targetTickMs * Math.Max(4.0, config.PacketGuardTpsMaxTimingMultiplier * 2.0);
				double num2 = Math.Clamp(num, targetTickMs, max);
				double num3 = ((num2 > smoothedTickMs) ? 0.3 : 0.045);
				smoothedTickMs += (num2 - smoothedTickMs) * num3;
				smoothedTickMs = Math.Clamp(smoothedTickMs, targetTickMs, max);
				sampleCount = Math.Min(sampleCount + 1, 1000000);
			}
		}
	}

	public PacketGuardTpsSnapshot GetSnapshot()
	{
		lock (sync)
		{
			double num = 1000.0 / Math.Max(1.0, smoothedTickMs);
			double num2 = Math.Clamp(num / Math.Max(0.1, targetTps) * 100.0, 0.0, 100.0);
			int num3 = Math.Clamp((int)Math.Ceiling(targetTps * 2.0), 12, 80);
			bool flag = sampleCount >= num3;
			string band;
			if (config.PacketGuardTpsAwarenessEnabled)
			{
				if (flag)
				{
					if (num2 < config.PacketGuardTpsCriticalPercent)
					{
						band = "Critical";
					}
					else
					{
						band = ((!(num2 < config.PacketGuardTpsDegradedPercent)) ? "Healthy" : "Degraded");
					}
				}
				else
				{
					band = "Warmup";
				}
			}
			else
			{
				band = "Disabled";
			}
			double timingMultiplier = 1.0;
			if (config.PacketGuardTpsAwarenessEnabled && num2 < config.PacketGuardTpsDegradedPercent)
			{
				timingMultiplier = Math.Clamp(targetTps / Math.Max(0.1, num), 1.0, config.PacketGuardTpsMaxTimingMultiplier);
			}
			bool suppressTimingRejections = (config.PacketGuardTpsAwarenessEnabled & flag) && config.PacketGuardTpsSuppressTimingRejectsWhenCritical && num2 < config.PacketGuardTpsCriticalPercent;
			return new PacketGuardTpsSnapshot(config.PacketGuardTpsAwarenessEnabled, flag, targetTps, num, num2, timingMultiplier, band, sampleCount, smoothedTickMs, suppressTimingRejections);
		}
	}

	public void Dispose()
	{
		if (!started)
		{
			return;
		}
		started = false;
		if (listenerId != 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(listenerId);
			}
			catch
			{
			}
			listenerId = 0L;
		}
	}
}
