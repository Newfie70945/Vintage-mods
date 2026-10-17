using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BlockLogger.AntiCheat;

internal sealed class EvidenceFileWriter
{
	private readonly Channel<byte[]> chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
	{
		SingleReader = true,
		SingleWriter = true,
		FullMode = BoundedChannelFullMode.Wait
	});

	private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

	private readonly string path;

	private readonly Task<string> work;

	public bool IsAborted => cancellation.IsCancellationRequested;

	public EvidenceFileWriter(string path)
	{
		this.path = path;
		work = Task.Run((Func<Task<string>?>)WriteAsync);
		work.ContinueWith((Task<string> t) =>
		{
			_ = t.Exception;
		}, TaskContinuationOptions.OnlyOnFaulted);
	}

	public bool TryWrite(byte[] bytes)
	{
		return !work.IsCompleted && chunks.Writer.TryWrite((byte[])bytes.Clone());
	}

	public Task<string> CompleteAsync()
	{
		chunks.Writer.TryComplete();
		return work;
	}

	public void Abort()
	{
		cancellation.Cancel();
		chunks.Writer.TryComplete();
		Task.Run(async () =>
		{
			try
			{
				await work.ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
			}
			try
			{
				File.Delete(path);
			}
			catch
			{
			}
		});
	}

	private async Task<string> WriteAsync()
	{
		try
		{
			using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await foreach (byte[] bytes in chunks.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(continueOnCapturedContext: false))
				{
					await stream.WriteAsync(bytes, cancellation.Token).ConfigureAwait(continueOnCapturedContext: false);
				}
				cancellation.Token.ThrowIfCancellationRequested();
				stream.Flush(flushToDisk: true);
			}
			cancellation.Token.ThrowIfCancellationRequested();
			using FileStream input = File.OpenRead(path);
			return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation.Token).ConfigureAwait(continueOnCapturedContext: false)).ToLowerInvariant();
		}
		catch
		{
			try
			{
				File.Delete(path);
			}
			catch
			{
			}
			throw;
		}
	}
}
