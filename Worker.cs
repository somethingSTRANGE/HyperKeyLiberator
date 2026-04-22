using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperKeyLiberator;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    // W, T, Y, O, P, D, L, X, N, Space
    private static readonly uint[] offendingKeys = [0x57, 0x54, 0x59, 0x4F, 0x50, 0x44, 0x4C, 0x58, 0x4E, 0x20];

    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_WIN      = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint Modifiers    = MOD_WIN | MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static uint FindProcessId(string processName)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
        try { return processes.Length > 0 ? (uint)processes[0].Id : 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    private static void WaitForProcessExit(uint pid, CancellationToken stoppingToken)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            while (!process.WaitForExit(1000) && !stoppingToken.IsCancellationRequested) { }
        }
        catch (ArgumentException) { } // Process already exited before we could open it
    }

    private void Takeover()
    {
        for (int i = 0; i < offendingKeys.Length; i++)
        {
            if (!RegisterHotKey(IntPtr.Zero, i, Modifiers, offendingKeys[i]))
                logger.LogWarning("RegisterHotKey failed for index {Index} (VK 0x{VK:X2}): Win32 error {Error}",
                    i, offendingKeys[i], Marshal.GetLastWin32Error());
        }
    }

    private void Relinquish()
    {
        for (int i = 0; i < offendingKeys.Length; i++)
            UnregisterHotKey(IntPtr.Zero, i);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // RegisterHotKey/UnregisterHotKey are bound to the calling thread's message queue,
        // so both must be called from the same dedicated thread — not the thread pool.
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { RunLoop(stoppingToken); }
            finally { tcs.SetResult(); }
        }) { IsBackground = true };
        thread.Start();
        return tcs.Task;
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Registering hotkey stubs");
            Takeover();

            logger.LogInformation("Waiting for explorer.exe");
            uint pid;
            while ((pid = FindProcessId("explorer.exe")) == 0)
            {
                if (stoppingToken.WaitHandle.WaitOne(1000)) { Relinquish(); return; }
            }

            logger.LogInformation("Explorer found (PID {Pid}), holding stubs for 4 seconds", pid);
            if (stoppingToken.WaitHandle.WaitOne(4000)) { Relinquish(); return; }

            logger.LogInformation("Releasing hotkey stubs");
            Relinquish();

            logger.LogInformation("Monitoring explorer.exe (PID {Pid}) for exit", pid);
            WaitForProcessExit(pid, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
                logger.LogInformation("Explorer exited, restarting cycle");
        }
    }
}