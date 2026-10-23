using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class PackagedSmoke
{
    private delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    internal static void Run(string executable, string output)
    {
        Directory.CreateDirectory(output);
        using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false })!;
        nint main = 0;
        var until = DateTime.UtcNow.AddSeconds(90);
        string observedTitle = "";
        bool noticeDismissed = false;
        while (!process.HasExited && DateTime.UtcNow < until && main == 0)
        {
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint id);
                if (id != process.Id || !IsWindowVisible(handle)) return true;
                var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                observedTitle = title.ToString();
                if (observedTitle == "Sunrise File Manager 1.5.4") main = handle;
                else if (!noticeDismissed && observedTitle == "Sunrise File Manager 1.5.4 · 공지사항 및 업데이트")
                {
                    NativeUiRegression.CaptureDesktop(Path.Combine(output, "packaged-startup-notice.png"));
                    noticeDismissed = true;
                    PostMessage(handle, 0x10, 0, 0); // Normal close respects the product's required-update exit policy.
                }
                return true;
            }, 0);
            Thread.Sleep(100);
        }
        try
        {
            if (main == 0) { NativeUiRegression.CaptureDesktop(Path.Combine(output, "packaged-startup-failure.png")); throw new Exception("Packaged startup did not reach the main window: " + observedTitle); }
            SetForegroundWindow(main); Thread.Sleep(1000);
            NativeUiRegression.CaptureDesktop(Path.Combine(output, "packaged-single.png"));
            foreach (byte key in new byte[] { 0x32, 0x34, 0x31 })
            {
                keybd_event(0x11, 0, 0, 0); keybd_event(key, 0, 0, 0);
                keybd_event(key, 0, 2, 0); keybd_event(0x11, 0, 2, 0); Thread.Sleep(500);
                NativeUiRegression.CaptureDesktop(Path.Combine(output, $"packaged-key-{key}.png"));
            }
            PostMessage(main, 0x10, 0, 0);
            if (!process.WaitForExit(15000) || process.ExitCode != 0) throw new Exception("Packaged clean exit failed.");
            File.WriteAllText(Path.Combine(output, "PACKAGED_RESULT.json"), JsonSerializer.Serialize(new { status = "PASS", executable, observedTitle, pid = process.Id, exitCode = process.ExitCode, inputs = "Native Ctrl+2 / Ctrl+4 / Ctrl+1", utc = DateTime.UtcNow }));
            Console.WriteLine("PACKAGED_STARTUP_AND_NATIVE_LAYOUT_INPUT_PASS");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
