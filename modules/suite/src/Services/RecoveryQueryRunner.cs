using Microsoft.Data.Sqlite;

namespace BlockLogger.Services;

// One admitted recovery reader per logger; no unbounded Task.Run query backlog.
internal sealed class RecoveryQueryRunner
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<T> RunAsync<T>(Func<SqliteConnection> open, Func<SqliteConnection, CancellationToken, T> execute,
        CancellationToken shutdown, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("A recovery query is already running; retry after it finishes.");
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var connection = open();
                // Microsoft.Data.Sqlite's synchronous Command.Cancel is a no-op.
                // Interrupt the native connection to stop execution inside SQLite.
                // A progress callback also closes the race where cancellation
                // happens after the token check but before the statement starts.
                SQLitePCL.raw.sqlite3_progress_handler(connection.Handle,1000,_=>token.IsCancellationRequested?1:0,null);
                try
                {
                    using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                    token.ThrowIfCancellationRequested();
                    var result = execute(connection, token);
                    token.ThrowIfCancellationRequested();
                    return result;
                }
                finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle,0,null,null); }
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            if (!shutdown.IsCancellationRequested && !cancellation.IsCancellationRequested)
                throw new TimeoutException("Recovery query exceeded its work deadline. Narrow the time window or area.", ex);
            throw new OperationCanceledException("Recovery query cancelled.", ex, token);
        }
        finally { gate.Release(); }
    }
}
