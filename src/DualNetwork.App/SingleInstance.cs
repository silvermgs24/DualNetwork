using System.Runtime.InteropServices;

namespace DualNetwork.App;

/// <summary>
/// One running instance per session. A second launch (even a non-elevated one, e.g. double-clicking the exe while
/// the elevated tray instance runs) signals the first instance to show its window, then exits.
/// The kernel objects get a permissive DACL and a Medium integrity label so a non-elevated process may signal them.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Sddl = "D:(A;;GA;;;WD)S:(ML;;NW;;;ME)";
    private const int ERROR_ALREADY_EXISTS = 183;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly string _name;
    private IntPtr _mutex;
    private IntPtr _event;
    private volatile bool _disposed;

    private SingleInstance(string name) => _name = name;

    public bool IsFirst { get; private set; }

    /// <param name="waitMs">How long to keep trying when another instance is still shutting down (after an elevation relaunch).</param>
    public static SingleInstance Acquire(string name, int waitMs = 0)
    {
        var si = new SingleInstance(name);
        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            si.TryCreate();
            if (si.IsFirst || Environment.TickCount64 >= deadline) return si;
            si.CloseMutex();
            Thread.Sleep(250);
        }
    }

    private void TryCreate()
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out var sd, IntPtr.Zero);
        sa.lpSecurityDescriptor = sd;
        try
        {
            _mutex = CreateMutexW(ref sa, false, $@"Local\{_name}");
            var err = Marshal.GetLastWin32Error();
            if (_mutex == IntPtr.Zero && sd != IntPtr.Zero)
            {
                // Fall back to default security if the descriptor was rejected.
                sa.lpSecurityDescriptor = IntPtr.Zero;
                _mutex = CreateMutexW(ref sa, false, $@"Local\{_name}");
                err = Marshal.GetLastWin32Error();
            }
            IsFirst = _mutex != IntPtr.Zero && err != ERROR_ALREADY_EXISTS;
            if (IsFirst) _event = CreateEventW(ref sa, false, false, $@"Local\{_name}.Show");
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public bool SignalFirstInstance()
    {
        var h = OpenEventW(EVENT_MODIFY_STATE, false, $@"Local\{_name}.Show");
        if (h == IntPtr.Zero) return false;
        SetEvent(h);
        CloseHandle(h);
        return true;
    }

    public void Listen(Action onShowRequested)
    {
        if (_event == IntPtr.Zero) return;
        new Thread(() =>
        {
            while (!_disposed && WaitForSingleObject(_event, INFINITE) == 0)
                if (!_disposed) onShowRequested();
        }) { IsBackground = true, Name = "SingleInstanceListener" }.Start();
    }

    private void CloseMutex()
    {
        if (_mutex != IntPtr.Zero) CloseHandle(_mutex);
        _mutex = IntPtr.Zero;
    }

    public void Dispose()
    {
        _disposed = true;
        CloseMutex();
        if (_event != IntPtr.Zero) { SetEvent(_event); CloseHandle(_event); _event = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateMutexW(ref SECURITY_ATTRIBUTES sa, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
}
