using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperKeyLiberator;

internal static class Program
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

    private static void Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        RunLoop(cts.Token);
    }

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

    private static void Takeover()
    {
        for (int i = 0; i < offendingKeys.Length; i++)
        {
            if (!RegisterHotKey(IntPtr.Zero, i, Modifiers, offendingKeys[i]))
                Console.WriteLine($"RegisterHotKey failed for index {i} (VK 0x{offendingKeys[i]:X2}): Win32 error {Marshal.GetLastWin32Error()}");
        }
    }

    private static void Relinquish()
    {
        for (int i = 0; i < offendingKeys.Length; i++)
            UnregisterHotKey(IntPtr.Zero, i);
    }

    private static void RunLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Console.WriteLine("Registering hotkey stubs");
            Takeover();

            Console.WriteLine("Waiting for explorer.exe");
            uint pid;
            while ((pid = FindProcessId("explorer.exe")) == 0)
            {
                if (stoppingToken.WaitHandle.WaitOne(1000)) { Relinquish(); return; }
            }

            Console.WriteLine($"Explorer found (PID {pid}), holding stubs for 4 seconds");
            if (stoppingToken.WaitHandle.WaitOne(4000)) { Relinquish(); return; }

            Console.WriteLine("Releasing hotkey stubs");
            Relinquish();

            Console.WriteLine($"Monitoring explorer.exe (PID {pid}) for exit");
            WaitForProcessExit(pid, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
                Console.WriteLine("Explorer exited, restarting cycle");
        }
    }
}
