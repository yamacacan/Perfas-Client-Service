using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkerService1.Services
{
    public class WinApiActiveWindowProvider : IActiveWindowProvider
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        public (int pid, string processName, string title) GetActiveWindowInfo()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return (0, "", "");

                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);
                int pid = (int)processId;

                string processName = "";
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        processName = process.ProcessName;
                    }
                }
                catch
                {
                    // Process might have exited or access denied
                }

                int length = GetWindowTextLength(hWnd);
                string title = "";
                if (length > 0)
                {
                    StringBuilder sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                return (pid, processName, title);
            }
            catch
            {
                return (0, "", "");
            }
        }
    }
}
