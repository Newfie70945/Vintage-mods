using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BlockLogger.Services;

internal sealed class RecoveryJournal : IAsyncDisposable
{
	private readonly string auditPath;

	private readonly string receiptPath;

	private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

	private FileStream? audit;

	private FileStream? receipts;

	private int disposed;

	public RecoveryJournal(string auditPath, string receiptPath)
	{
		this.auditPath = auditPath;
		this.receiptPath = receiptPath;
	}

	public async Task AppendAsync(object auditRecord, object? receiptRecord = null)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(in disposed) != 0, this);
		await gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			await Task.Run(() =>
			{
				if (audit == null)
				{
					audit = Open(auditPath);
				}
				Write(audit, auditRecord);
				if (receiptRecord != null)
				{
					if (receipts == null)
					{
						receipts = Open(receiptPath);
					}
					Write(receipts, receiptRecord);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			gate.Release();
		}
	}

	private static FileStream Open(string path)
	{
		return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
	}

	private static void Write(FileStream file, object record)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record) + Environment.NewLine);
		file.Write(bytes);
		file.Flush(flushToDisk: true);
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		await gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			await Task.Run(() =>
			{
				audit?.Dispose();
				receipts?.Dispose();
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			gate.Release();
			gate.Dispose();
		}
	}
}
