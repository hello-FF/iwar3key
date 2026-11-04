using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IWar3Key.Shell.Interop
{
    public struct InjectionResult
    {
        public bool Success;
        public bool AlreadyLoaded;
        public string Message;
    }

    public static class Injector
    {
        public static InjectionResult Inject(int pid, string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                return Fail("Agent 文件不存在: " + dllPath);
            }

            IntPtr process = IntPtr.Zero;
            IntPtr remote = IntPtr.Zero;
            IntPtr thread = IntPtr.Zero;
            try
            {
                process = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_CREATE_THREAD |
                    NativeMethods.PROCESS_QUERY_INFORMATION |
                    NativeMethods.PROCESS_VM_OPERATION |
                    NativeMethods.PROCESS_VM_WRITE |
                    NativeMethods.PROCESS_VM_READ,
                    false,
                    pid);

                if (process == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 5)
                    {
                        return Fail("打开游戏进程被拒绝。请以管理员身份运行 iWar3Key。");
                    }

                    return Fail("打开游戏进程失败，Win32 错误 " + error + "。");
                }

                if (Environment.Is64BitOperatingSystem)
                {
                    if (!NativeMethods.IsWow64Process(process, out bool wow64) || !wow64)
                    {
                        return Fail("目标进程不是 32 位进程，无法加载 x86 Agent。");
                    }
                }

                if (IsModuleLoaded(process, "iwar3key32.dll"))
                {
                    return new InjectionResult
                    {
                        Success = true,
                        AlreadyLoaded = true,
                        Message = "Agent 已在游戏进程中。"
                    };
                }

                byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
                remote = NativeMethods.VirtualAllocEx(
                    process,
                    IntPtr.Zero,
                    new UIntPtr((uint)pathBytes.Length),
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                    NativeMethods.PAGE_READWRITE);

                if (remote == IntPtr.Zero)
                {
                    return Fail("在游戏进程中分配内存失败，Win32 错误 " + Marshal.GetLastWin32Error() + "。");
                }

                if (!NativeMethods.WriteProcessMemory(
                        process,
                        remote,
                        pathBytes,
                        new UIntPtr((uint)pathBytes.Length),
                        out UIntPtr written) ||
                    written.ToUInt64() != (ulong)pathBytes.Length)
                {
                    return Fail("写入 Agent 路径失败，Win32 错误 " + Marshal.GetLastWin32Error() + "。");
                }

                IntPtr kernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
                IntPtr loadLibrary = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
                if (loadLibrary == IntPtr.Zero)
                {
                    return Fail("无法解析 LoadLibraryW。");
                }

                thread = NativeMethods.CreateRemoteThread(
                    process,
                    IntPtr.Zero,
                    0,
                    loadLibrary,
                    remote,
                    0,
                    out _);

                if (thread == IntPtr.Zero)
                {
                    return Fail("创建远程线程失败，Win32 错误 " + Marshal.GetLastWin32Error() + "。");
                }

                uint wait = NativeMethods.WaitForSingleObject(thread, 15000);
                if (wait == 258)
                {
                    return Fail("等待 Agent 加载超时。游戏可能卡住，请重启游戏后再试。");
                }

                if (!NativeMethods.GetExitCodeThread(thread, out uint exitCode))
                {
                    return Fail("读取远程线程结果失败，Win32 错误 " + Marshal.GetLastWin32Error() + "。");
                }

                if (exitCode == 0)
                {
                    return Fail("LoadLibraryW 加载 Agent 失败。请确认 Agent 为 32 位且路径可访问。");
                }

                return new InjectionResult
                {
                    Success = true,
                    AlreadyLoaded = false,
                    Message = "Agent 已注入。"
                };
            }
            catch (Exception ex)
            {
                return Fail("注入异常: " + ex.Message);
            }
            finally
            {
                if (thread != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(thread);
                }

                if (remote != IntPtr.Zero && process != IntPtr.Zero)
                {
                    NativeMethods.VirtualFreeEx(process, remote, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }

                if (process != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(process);
                }
            }
        }

        private static bool IsModuleLoaded(IntPtr process, string moduleName)
        {
            var modules = new IntPtr[1024];
            if (!NativeMethods.EnumProcessModulesEx(
                    process,
                    modules,
                    (uint)(modules.Length * IntPtr.Size),
                    out uint needed,
                    NativeMethods.LIST_MODULES_ALL))
            {
                return false;
            }

            int count = Math.Min(modules.Length, (int)(needed / (uint)IntPtr.Size));
            var name = new StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                name.Clear();
                if (NativeMethods.GetModuleBaseNameW(process, modules[i], name, (uint)name.Capacity) > 0 &&
                    name.ToString().Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static InjectionResult Fail(string message)
        {
            return new InjectionResult { Success = false, Message = message };
        }
    }
}
