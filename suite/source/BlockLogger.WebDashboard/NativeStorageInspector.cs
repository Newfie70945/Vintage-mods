using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BlockLogger.WebDashboard;

internal static class NativeStorageInspector
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Close(nint db);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Prepare(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Step(nint statement);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint TextColumn(nint statement, int column);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate long IntColumn(nint statement, int column);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Finish(nint statement);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int ProgressCallback(nint data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void Progress(nint db, int instructions, ProgressCallback callback, nint data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void Interrupt(nint db);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Busy(nint db, int ms);

	public static Dictionary<string, (long Bytes, long Cells)>? TryRead(string path, out string message)
	{
		message = "System SQLite page statistics unavailable.";
		string[] array;
		if (OperatingSystem.IsWindows())
		{
			array = new string[2] { "sqlite3.dll", "winsqlite3.dll" };
		}
		else
		{
			array = ((!OperatingSystem.IsMacOS()) ? new string[2] { "libsqlite3.so.0", "libsqlite3.so" } : new string[1] { "libsqlite3.dylib" });
		}
		foreach (string libraryPath in array)
		{
			if (!NativeLibrary.TryLoad(libraryPath, out var library))
			{
				continue;
			}
			nint db = IntPtr.Zero;
			nint statement = IntPtr.Zero;
			Close close = null;
			Finish finish = null;
			ProgressCallback progressCallback = null;
			Timer timer = null;
			object handleLock = new object();
			try
			{
				close = Bind<Close>("sqlite3_close_v2");
				finish = Bind<Finish>("sqlite3_finalize");
				if (Bind<Open>("sqlite3_open_v2")(path, out db, 65537, IntPtr.Zero) != 0 || db == IntPtr.Zero)
				{
					continue;
				}
				Bind<Busy>("sqlite3_busy_timeout")(db, 500);
				Stopwatch clock = Stopwatch.StartNew();
				Interrupt interrupt = Bind<Interrupt>("sqlite3_interrupt");
				timer = new Timer((object? _) =>
				{
					lock (handleLock)
					{
						if (db != IntPtr.Zero)
						{
							interrupt(db);
						}
					}
				}, null, 12000, -1);
				progressCallback = (nint _) => (clock.ElapsedMilliseconds >= 12000) ? 1 : 0;
				Bind<Progress>("sqlite3_progress_handler")(db, 1000, progressCallback, IntPtr.Zero);
				if (Bind<Prepare>("sqlite3_prepare_v2")(db, "SELECT name,pgsize,ncell FROM dbstat WHERE aggregate=TRUE;", -1, out statement, IntPtr.Zero) != 0)
				{
					continue;
				}
				Step step = Bind<Step>("sqlite3_step");
				TextColumn textColumn = Bind<TextColumn>("sqlite3_column_text");
				IntColumn intColumn = Bind<IntColumn>("sqlite3_column_int64");
				Dictionary<string, (long, long)> dictionary = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
				int num;
				while ((num = step(statement)) == 100)
				{
					string text = Marshal.PtrToStringUTF8(textColumn(statement, 0)) ?? "";
					if (text.Length > 0)
					{
						dictionary[text] = (Math.Max(0L, intColumn(statement, 1)), Math.Max(0L, intColumn(statement, 2)));
					}
				}
				if (num == 101)
				{
					message = "Exact page ownership from independent read-only system SQLite inspection.";
					return dictionary;
				}
				if (clock.ElapsedMilliseconds >= 12000)
				{
					message = "Page inspection reached its 12-second limit; using bounded payload estimates.";
					return null;
				}
				message = ((clock.ElapsedMilliseconds >= 12000) ? "Page inspection reached its 12-second limit; using bounded payload estimates." : "System page inspection was interrupted or unavailable.");
			}
			catch (Exception ex)
			{
				message = "System page inspection unavailable: " + ex.Message;
			}
			finally
			{
				if (statement != IntPtr.Zero)
				{
					finish?.Invoke(statement);
				}
				timer?.Dispose();
				lock (handleLock)
				{
					if (db != IntPtr.Zero)
					{
						close?.Invoke(db);
					}
					db = IntPtr.Zero;
				}
				GC.KeepAlive(progressCallback);
				NativeLibrary.Free(library);
			}
			T Bind<T>(string name) where T : notnull, Delegate
			{
				return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
			}
		}
		return null;
	}
}
