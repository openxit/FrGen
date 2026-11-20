using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFrameGen.Core.Services;

public sealed record TargetWindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
        ? Title
        : $"{ProcessName} — {Title}";
}

public static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    public static IReadOnlyList<TargetWindowInfo> GetCaptureableWindows()
    {
        var results = new List<TargetWindowInfo>();
        nint shellWindow = GetShellWindow();
        int selfPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow || !IsWindowVisible(hWnd))
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
                return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == (uint)selfPid)
                return true;

            string procName = "";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch
            {
                // Process may have exited or denied access
            }

            if (string.Equals(procName, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            results.Add(new TargetWindowInfo(hWnd, title, procName, (int)pid));
            return true;
        }, nint.Zero);

        return results;
    }
}
