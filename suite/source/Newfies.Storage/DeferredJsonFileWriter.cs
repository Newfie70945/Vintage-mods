using System;
using System.IO;
using System.Threading;

namespace Newfies.Storage;

internal sealed class DeferredJsonFileWriter : IDisposable
{
	private readonly string path;

	private readonly Action<string> reportError;

	private readonly object gate = new object();

	private readonly AutoResetEvent signal = new AutoResetEvent(initialState: false);

	private readonly Thread worker;

	private Func<byte[]>? pending;

	private long requested;

	private long completed;

	private bool stopping;

	private string lastError = "";

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

	public DeferredJsonFileWriter(string path, Action<string> reportError)
	{
		this.path = path;
		this.reportError = reportError;
		worker = new Thread(Run)
		{
			IsBackground = true,
			Name = "NSS JSON writer " + Path.GetFileName(path)
		};
		worker.Start();
	}

	public bool Queue(Func<byte[]> serializeImmutableSnapshot)
	{
		lock (gate)
		{
			if (stopping)
			{
				return false;
			}
			pending = serializeImmutableSnapshot;
			requested++;
		}
		signal.Set();
		return true;
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
				Func<byte[]> func;
				long num;
				lock (gate)
				{
					func = pending;
					pending = null;
					num = requested;
					if (func == null)
					{
						if (stopping)
						{
							return;
						}
						break;
					}
				}
				try
				{
					byte[] array = func();
					if (array.LongLength > 67108864)
					{
						throw new InvalidDataException("Serialized state exceeds the 64 MiB safety limit; previous file retained.");
					}
					string sourceFileName = path + ".tmp";
					using (FileStream fileStream = new FileStream(sourceFileName, FileMode.Create, FileAccess.Write, FileShare.None))
					{
						fileStream.Write(array);
						fileStream.Flush(flushToDisk: true);
					}
					File.Move(sourceFileName, path, overwrite: true);
					lock (gate)
					{
						completed = num;
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
						if (pending == null)
						{
							pending = func;
						}
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
			reportError("Writer did not finish within shutdown budget; pending data was not confirmed durable.");
		}
		catch
		{
		}
	}
}
