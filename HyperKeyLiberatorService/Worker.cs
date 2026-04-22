using System.Runtime.InteropServices;

namespace HyperKeyLiberatorService;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    private readonly Dictionary<uint, int> _helpers = new();
    private readonly HashSet<uint> _activeSessions = new();
    private readonly List<uint> _toKill = new();
    private readonly string _helperPath = Path.Combine(AppContext.BaseDirectory, "HyperKeyLiberator.exe");

    private enum WTS_CONNECTSTATE_CLASS { WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName; // LPWSTR — unused, kept as IntPtr to avoid string allocation
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    private static readonly int WtsSessionInfoSize = Marshal.SizeOf<WTS_SESSION_INFO>();
    private const uint CREATE_UNICODE_ENVIRONMENT        = 0x00000400;
    private const uint CREATE_NO_WINDOW                  = 0x08000000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000;
    private const uint PROCESS_TERMINATE                 = 0x00000001;
    private const uint STILL_ACTIVE                      = 259;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Service started, helper: {HelperPath}", _helperPath);
        return Task.Run(() => RunLoop(stoppingToken));
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SyncHelpers();
                stoppingToken.WaitHandle.WaitOne(1000);
            }
        }
        finally { KillAllHelpers(); }
    }

    private void SyncHelpers()
    {
        GetActiveSessionIds(_activeSessions);

        foreach (var sessionId in _activeSessions)
        {
            if (_helpers.TryGetValue(sessionId, out var pid))
            {
                if (IsProcessRunning(pid)) continue;
                _helpers.Remove(sessionId);
                logger.LogWarning("Helper for session {SessionId} exited unexpectedly, re-spawning", sessionId);
            }
            SpawnHelper(sessionId);
        }

        _toKill.Clear();
        foreach (var sessionId in _helpers.Keys)
            if (!_activeSessions.Contains(sessionId))
                _toKill.Add(sessionId);
        foreach (var sessionId in _toKill)
            KillHelper(sessionId);
    }

    private void GetActiveSessionIds(HashSet<uint> result)
    {
        result.Clear();
        if (!WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out var pSessions, out int count))
            return;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(pSessions, i * WtsSessionInfoSize));
                if (info.State == WTS_CONNECTSTATE_CLASS.WTSActive && info.SessionId != 0)
                    result.Add(info.SessionId);
            }
        }
        finally { WTSFreeMemory(pSessions); }
    }

    private static bool IsProcessRunning(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return GetExitCodeProcess(handle, out uint code) && code == STILL_ACTIVE;
        }
        finally { CloseHandle(handle); }
    }

    private void SpawnHelper(uint sessionId)
    {
        if (!File.Exists(_helperPath))
        {
            logger.LogError("Helper not found: {HelperPath}", _helperPath);
            return;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            logger.LogWarning("WTSQueryUserToken failed for session {SessionId}: error {Error}", sessionId, Marshal.GetLastWin32Error());
            return;
        }
        try
        {
            CreateEnvironmentBlock(out var environment, userToken, false);
            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                if (!CreateProcessAsUser(userToken, _helperPath, _helperPath, IntPtr.Zero, IntPtr.Zero,
                        false, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        environment, null, ref si, out var pi))
                {
                    logger.LogWarning("CreateProcessAsUser failed for session {SessionId}: error {Error}", sessionId, Marshal.GetLastWin32Error());
                    return;
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                _helpers[sessionId] = pi.dwProcessId;
                logger.LogInformation("Spawned helper (PID {Pid}) for session {SessionId}", pi.dwProcessId, sessionId);
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            }
        }
        finally { CloseHandle(userToken); }
    }

    private void KillHelper(uint sessionId)
    {
        if (!_helpers.TryGetValue(sessionId, out var pid)) return;
        _helpers.Remove(sessionId);
        var handle = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (handle == IntPtr.Zero)
        {
            logger.LogInformation("Helper for session {SessionId} already exited", sessionId);
            return;
        }
        try
        {
            if (TerminateProcess(handle, 0))
                logger.LogInformation("Killed helper (PID {Pid}) for session {SessionId}", pid, sessionId);
            else
                logger.LogWarning("TerminateProcess failed for helper (PID {Pid}), session {SessionId}: error {Error}", pid, sessionId, Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(handle); }
    }

    private void KillAllHelpers()
    {
        foreach (var sessionId in _helpers.Keys.ToList())
            KillHelper(sessionId);
    }
}