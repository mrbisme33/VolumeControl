using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VolumeControl
{
    /// <summary>Best-effort process lookup. Protected or elevated processes fall back to name only (B5).</summary>
    internal static class ProcessInfo
    {
        private const int ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, uint pid);

        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("ntdll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);

        public static void Read(uint pid, out string exe, out string path, out string commandLine)
        {
            exe = null;
            path = null;
            commandLine = "";

            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(handle, 0, sb, ref size))
                    {
                        path = sb.ToString();
                        exe = Path.GetFileName(path);
                    }
                    commandLine = ReadCommandLine(handle);
                }
                catch (Exception) { /* fall through to name matching */ }
                finally { CloseHandle(handle); }
            }

            if (string.IsNullOrEmpty(exe))
            {
                try
                {
                    using (var p = Process.GetProcessById((int)pid))
                        exe = p.ProcessName + ".exe";
                }
                catch (Exception) { exe = "pid-" + pid; }
            }
        }

        private static string ReadCommandLine(IntPtr process)
        {
            NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return "";
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                    return "";
                // Buffer starts with a UNICODE_STRING: Length, MaximumLength, then an aligned pointer.
                int bytes = (ushort)Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
