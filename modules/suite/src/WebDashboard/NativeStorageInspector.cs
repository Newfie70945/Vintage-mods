using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlockLogger.WebDashboard;

/// <summary>Independent read-only inspector. Does not replace the game's shared SQLite provider.</summary>
internal static class NativeStorageInspector
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out IntPtr db,int flags,IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Prepare(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int bytes,out IntPtr statement,IntPtr tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(IntPtr statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr TextColumn(IntPtr statement,int column);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long IntColumn(IntPtr statement,int column);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Finish(IntPtr statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgressCallback(IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Progress(IntPtr db,int instructions,ProgressCallback callback,IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Interrupt(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Busy(IntPtr db,int ms);

    public static Dictionary<string,(long Bytes,long Cells)>? TryRead(string path,out string message)
    {
        message="System SQLite page statistics unavailable.";
        foreach(string candidate in OperatingSystem.IsWindows()?new[]{"sqlite3.dll","winsqlite3.dll"}:OperatingSystem.IsMacOS()?new[]{"libsqlite3.dylib"}:new[]{"libsqlite3.so.0","libsqlite3.so"})
        {
            if(!NativeLibrary.TryLoad(candidate,out var library))continue;
            IntPtr db=IntPtr.Zero,statement=IntPtr.Zero;
            T Bind<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));
            Close? close=null;Finish? finish=null;ProgressCallback? callback=null;Timer? timer=null;object handleLock=new();
            try
            {
                close=Bind<Close>("sqlite3_close_v2");finish=Bind<Finish>("sqlite3_finalize");
                int rc=Bind<Open>("sqlite3_open_v2")(path,out db,1|0x10000,IntPtr.Zero); // READONLY + FULLMUTEX
                if(rc!=0||db==IntPtr.Zero)continue;
                Bind<Busy>("sqlite3_busy_timeout")(db,500);
                var clock=Stopwatch.StartNew();var interrupt=Bind<Interrupt>("sqlite3_interrupt");timer=new Timer(_=>{lock(handleLock){if(db!=IntPtr.Zero)interrupt(db);}},null,12000,Timeout.Infinite);callback=_=>clock.ElapsedMilliseconds>=12000?1:0;
                Bind<Progress>("sqlite3_progress_handler")(db,1000,callback,IntPtr.Zero);
                rc=Bind<Prepare>("sqlite3_prepare_v2")(db,"SELECT name,pgsize,ncell FROM dbstat WHERE aggregate=TRUE;",-1,out statement,IntPtr.Zero);
                if(rc!=0)continue;
                var step=Bind<Step>("sqlite3_step");var text=Bind<TextColumn>("sqlite3_column_text");var integer=Bind<IntColumn>("sqlite3_column_int64");
                var result=new Dictionary<string,(long,long)>(StringComparer.OrdinalIgnoreCase);
                while((rc=step(statement))==100)
                {
                    string name=Marshal.PtrToStringUTF8(text(statement,0))??"";
                    if(name.Length>0)result[name]=(Math.Max(0,integer(statement,1)),Math.Max(0,integer(statement,2)));
                }
                if(rc==101){message="Exact page ownership from independent read-only system SQLite inspection.";return result;}
                if(clock.ElapsedMilliseconds>=12000){message="Page inspection reached its 12-second limit; using bounded payload estimates.";return null;}
                message=clock.ElapsedMilliseconds>=12000?"Page inspection reached its 12-second limit; using bounded payload estimates.":"System page inspection was interrupted or unavailable.";
            }
            catch(Exception ex){message="System page inspection unavailable: "+ex.Message;}
            finally
            {
                if(statement!=IntPtr.Zero)finish?.Invoke(statement);
                timer?.Dispose();lock(handleLock){if(db!=IntPtr.Zero)close?.Invoke(db);db=IntPtr.Zero;}
                GC.KeepAlive(callback);NativeLibrary.Free(library);
            }
        }
        return null;
    }
}
