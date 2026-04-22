using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperKeyLiberatorService;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    private readonly Dictionary<uint, Process> _helpers = new();
    private readonly string _helperPath = Path.Combine(AppContext.BaseDirectory, "HyperKeyLiberator.exe");

    private enum WTS_CONNECTSTATE_CLASS { WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
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
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW           = 0x08000000;

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
    private static extern bool CloseHandle(IntPtr hObject);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Service started, helper: {HelperPath}", _helperPath);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SyncHelpers();
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { KillAllHelpers(); }
    }

    private void SyncHelpers()
    {
        var activeSessions = GetActiveSessionIds();

        foreach (var sessionId in activeSessions)
        {
            if (_helpers.TryGetValue(sessionId, out var existing))
            {
                if (!existing.HasExited) continue;
                existing.Dispose();
                _helpers.Remove(sessionId);
                logger.LogWarning("Helper for session {SessionId} exited unexpectedly, re-spawning", sessionId);
            }
            SpawnHelper(sessionId);
        }

        foreach (var sessionId in _helpers.Keys.Except(activeSessions).ToList())
            KillHelper(sessionId);
    }

    private HashSet<uint> GetActiveSessionIds()
    {
        var result = new HashSet<uint>();
        if (!WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out var pSessions, out int count))
            return result;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(pSessions, i * size));
                if (info.State == WTS_CONNECTSTATE_CLASS.WTSActive && info.SessionId != 0)
                    result.Add(info.SessionId);
            }
        }
        finally { WTSFreeMemory(pSessions); }
        return result;
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
                try { _helpers[sessionId] = Process.GetProcessById(pi.dwProcessId); }
                finally { CloseHandle(pi.hProcess); }
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
        if (!_helpers.TryGetValue(sessionId, out var process)) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                logger.LogInformation("Killed helper for session {SessionId}", sessionId);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to kill helper for session {SessionId}", sessionId); }
        finally { process.Dispose(); _helpers.Remove(sessionId); }
    }

    private void KillAllHelpers()
    {
        foreach (var sessionId in _helpers.Keys.ToList())
            KillHelper(sessionId);
    }
}
