using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Newfies.Storage;

internal sealed class BoundedAuditWriter : IDisposable
{
	internal const long MaximumFileBytes = 8388608L;

	private const int MaximumQueuedBytes = 4194304;

	private readonly string path;

	private readonly Action<string> reportError;

	private readonly object gate = new object();

	private readonly Queue<byte[]> queue = new Queue<byte[]>();

	private readonly AutoResetEvent signal = new AutoResetEvent(initialState: false);

	private readonly Thread worker;

	private int queuedBytes;

	private long requested;

	private long completed;

	private long dropped;

	private long lastDropWarning;

	private bool stopping;

	private string lastError = "";

	public long Dropped
	{
		get
		{
			lock (gate)
			{
				return dropped;
			}
		}
	}

	public string LastError
	{
		get
		{
			lock (gate)
			{
				return lastError;
			}
		}
	}

	public BoundedAuditWriter(string path, Action<string> reportError)
	{
		this.path = path;
		this.reportError = reportError;
		worker = new Thread(Run)
		{
			IsBackground = true,
			Name = "NSS audit writer"
		};
		worker.Start();
	}

	public bool Queue(string jsonLine)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
		bool flag = false;
		lock (gate)
		{
			if (stopping)
			{
				return false;
			}
			if (bytes.Length <= 65536 && queue.Count < 1024 && queuedBytes + bytes.Length <= 4194304)
			{
				queue.Enqueue(bytes);
				queuedBytes += bytes.Length;
				requested++;
				signal.Set();
				return true;
			}
			dropped++;
			if (Environment.TickCount64 - lastDropWarning >= 30000)
			{
				lastDropWarning = Environment.TickCount64;
				flag = true;
			}
		}
		if (flag)
		{
			try
			{
				reportError("Audit queue reached its safety limit; dropped entries are counted in ChatGuard diagnostics.");
			}
			catch
			{
			}
		}
		return false;
	}

	public bool Flush(int milliseconds = 5000)
	{
		long num = Environment.TickCount64 + milliseconds;
		lock (gate)
		{
			long num2 = requested;
			signal.Set();
			while (completed < num2)
			{
				if (lastError.Length > 0)
				{
					return false;
				}
				int num3 = (int)Math.Max(0L, num - Environment.TickCount64);
				if (num3 == 0 || !Monitor.Wait(gate, num3))
				{
					return completed >= num2;
				}
			}
			return true;
		}
	}

	private void Run()
	{
		while (true)
		{
			signal.WaitOne();
			while (true)
			{
				byte[] array;
				lock (gate)
				{
					if (queue.Count == 0)
					{
						if (stopping)
						{
							return;
						}
						break;
					}
					array = queue.Peek();
				}
				try
				{
					if (File.Exists(path) && new FileInfo(path).Length + array.Length > 8388608)
					{
						File.Delete(path + ".4");
						for (int num = 3; num >= 1; num--)
						{
							if (File.Exists(path + "." + num))
							{
								File.Move(path + "." + num, path + "." + (num + 1), overwrite: true);
							}
						}
						File.Move(path, path + ".1", overwrite: true);
					}
					using (FileStream fileStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
					{
						fileStream.Write(array);
						fileStream.Flush(flushToDisk: true);
					}
					lock (gate)
					{
						queue.Dequeue();
						queuedBytes -= array.Length;
						completed++;
						lastError = "";
						Monitor.PulseAll(gate);
					}
				}
				catch (Exception ex)
				{
					bool flag;
					bool flag2;
					lock (gate)
					{
						flag = lastError != ex.Message;
						lastError = ex.Message;
						flag2 = stopping;
						Monitor.PulseAll(gate);
					}
					if (flag)
					{
						try
						{
							reportError(ex.Message);
						}
						catch
						{
						}
					}
					if (flag2)
					{
						return;
					}
					signal.WaitOne(2000);
				}
			}
		}
	}

	public void Dispose()
	{
		lock (gate)
		{
			if (stopping)
			{
				return;
			}
			stopping = true;
		}
		signal.Set();
		if (worker.Join(5000))
		{
			signal.Dispose();
			return;
		}
		try
		{
			reportError("Audit writer did not finish within the shutdown budget.");
		}
		catch
		{
		}
	}
}
