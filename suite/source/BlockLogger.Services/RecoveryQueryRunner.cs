using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BlockLogger.Services;

internal sealed class RecoveryQueryRunner
{
	private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

	public async Task<T> RunAsync<T>(Func<SqliteConnection> open, Func<SqliteConnection, CancellationToken, T> execute, CancellationToken shutdown, CancellationToken cancellation, TimeSpan? timeout = null)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancellation);
		deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30L));
		CancellationToken token = deadline.Token;
		if (!(await gate.WaitAsync(0, token).ConfigureAwait(continueOnCapturedContext: false)))
		{
			throw new InvalidOperationException("A recovery query is already running; retry after it finishes.");
		}
		try
		{
			return await Task.Run(() =>
			{
				token.ThrowIfCancellationRequested();
				SqliteConnection connection = open();
				try
				{
					raw.sqlite3_progress_handler(connection.Handle, 1000, (object _) => token.IsCancellationRequested ? 1 : 0, null);
					try
					{
						using (token.Register(() =>
						{
							raw.sqlite3_interrupt(connection.Handle);
						}))
						{
							token.ThrowIfCancellationRequested();
							T result = execute(connection, token);
							token.ThrowIfCancellationRequested();
							return result;
						}
					}
					finally
					{
						raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
					}
				}
				finally
				{
					if (connection != null)
					{
						((IDisposable)connection).Dispose();
					}
				}
			}, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception innerException) when (token.IsCancellationRequested)
		{
			if (!shutdown.IsCancellationRequested && !cancellation.IsCancellationRequested)
			{
				throw new TimeoutException("Recovery query exceeded its work deadline. Narrow the time window or area.", innerException);
			}
			throw new OperationCanceledException("Recovery query cancelled.", innerException, token);
		}
		finally
		{
			gate.Release();
		}
	}
}
