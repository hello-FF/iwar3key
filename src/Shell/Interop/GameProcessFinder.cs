using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IWar3Key.Shell.Interop
{
    public sealed class GameProcessInfo
    {
        public int Pid;
        public string ExePath;
        public string VersionText;
        public IntPtr Window;
        public bool PathMatchesPreference;
        public DateTime StartTime;

        public bool HasWindow => Window != IntPtr.Zero;
    }

    public static class GameProcessFinder
    {
        private static readonly Regex VersionRegex = new Regex(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

        public static List<GameProcessInfo> FindCandidates(string preferredPath)
        {
            var result = new List<GameProcessInfo>();
            foreach (string name in new[] { "war3", "Warcraft III" })
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (Process process in processes)
                {
                    try
                    {
                        GameProcessInfo info = BuildInfo(process, preferredPath);
                        if (info != null)
                        {
                            result.Add(info);
                        }
                    }
                    catch
                    {
                        // Ignore processes that disappear while enumerating.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return result
                .OrderByDescending(p => p.PathMatchesPreference)
                .ThenByDescending(p => p.HasWindow)
                .ThenByDescending(p => p.StartTime)
                .ToList();
        }

        public static GameProcessInfo BuildInfo(Process process, string preferredPath)
        {
            string exePath = GetProcessPath(process);
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return null;
            }

            if (!Path.GetFileName(exePath).Equals("war3.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string dir = Path.GetDirectoryName(exePath);
            string gameDll = dir == null ? null : Path.Combine(dir, "Game.dll");
            string version = null;
            if (gameDll != null && File.Exists(gameDll))
            {
                version = ReadVersion(gameDll);
            }

            bool matches = string.IsNullOrWhiteSpace(preferredPath) ||
                           Path.GetFullPath(exePath).Equals(Path.GetFullPath(preferredPath), StringComparison.OrdinalIgnoreCase);

            return new GameProcessInfo
            {
                Pid = process.Id,
                ExePath = exePath,
                VersionText = version,
                Window = FindWindow(process.Id),
                PathMatchesPreference = matches,
                StartTime = SafeStartTime(process)
            };
        }

        public static IntPtr FindWindow(int pid)
        {
            IntPtr best = IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid != (uint)pid)
                {
                    return true;
                }

                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                var className = new StringBuilder(256);
                NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
                if (className.ToString().Equals("Warcraft III", StringComparison.OrdinalIgnoreCase))
                {
                    best = hwnd;
                    return false;
                }

                if (fallback == IntPtr.Zero)
                {
                    fallback = hwnd;
                }

                return true;
            }, IntPtr.Zero);

            return best != IntPtr.Zero ? best : fallback;
        }

        public static string GetProcessPath(Process process)
        {
            IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buffer = new StringBuilder(1024);
                    int size = buffer.Capacity;
                    if (NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                    {
                        return buffer.ToString(0, size);
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }

            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        public static string ReadVersion(string gameDllPath)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(gameDllPath);
                if (info.FileMajorPart != 0 || info.FileMinorPart != 0 || info.FileBuildPart != 0 || info.FilePrivatePart != 0)
                {
                    return string.Format(
                        "{0}.{1}.{2}.{3}",
                        info.FileMajorPart,
                        info.FileMinorPart,
                        info.FileBuildPart,
                        info.FilePrivatePart);
                }

                string raw = info.FileVersion;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                Match match = VersionRegex.Match(raw.Replace(',', '.'));
                return match.Success ? match.Value : raw.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static DateTime SafeStartTime(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
