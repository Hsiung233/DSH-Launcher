using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// DshService 的进程-作业(Job Object)绑定部分。
    /// 把启动的 dsh 进程树放入一个带 KILL_ON_JOB_CLOSE 标志的 Windows 作业对象:
    /// 作业句柄由本应用进程持有,当应用进程意外终止(崩溃、被任务管理器强杀、断电等)
    /// 时内核自动关闭句柄并杀掉作业内全部进程;正常退出路径则由 Stop/显式关闭兜底。
    /// </summary>
    public sealed partial class DshService
    {
        /// <summary>与 dsh 进程树关联的作业对象句柄;一次启动对应一个新作业。</summary>
        private IntPtr _jobHandle;

        /// <summary>
        /// 把进程分配到新的"关闭即杀进程树"作业中,失败静默(只影响意外退出时的兜底清理)。
        /// 返回的作业句柄随本次 dsh 进程生命周期,在下一次启动时关闭旧句柄。
        /// 非 Windows 平台直接跳过:正常退出(含 mac Cmd+Q,由 App.ShutdownRequested 兜底)
        /// 与用户点停止的路径都走 <see cref="Process.Kill(entireProcessTree: true)"/>
        /// (.NET 的 KillTree 本身跨平台:SIGSTOP→递归子进程→SIGKILL);
        /// 仅"应用被强杀且来不及执行任何托管代码"这一场景在 macOS 无兜底
        /// (纯托管 API 无 setpgid,按约定不引入平台 P/Invoke)。
        /// </summary>
        private void AttachProcessToJob(Process process)
        {
            // macOS/Linux 没有作业对象;P/Invoke kernel32 会抛 DllNotFoundException,必须早退
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var job = NativeJob.CreateKillOnCloseJob();
            if (job == IntPtr.Zero || !NativeJob.AssignProcessToJob(job, process.Handle))
            {
                // 绑定失败不影响正常启停功能,仅失去意外退出时的自动清理能力
                if (job != IntPtr.Zero)
                {
                    NativeJob.Close(job);
                }
                AppLogService.Write("[Job] 将 dsh 进程绑定到作业对象失败,意外退出时可能无法自动停止服务");
                return;
            }

            // 启动新的 dsh 前先释放上一次的作业句柄(旧进程已停止,句柄的 KillOnClose 不再有目标)
            if (this._jobHandle != IntPtr.Zero)
            {
                NativeJob.Close(this._jobHandle);
            }
            this._jobHandle = job;
        }

        /// <summary>主动停止时调用的兜底:关闭作业句柄(内核会杀掉作业内残留进程,若还有的话)。</summary>
        private void CloseJobHandle()
        {
            if (this._jobHandle != IntPtr.Zero)
            {
                NativeJob.Close(this._jobHandle);
                this._jobHandle = IntPtr.Zero;
            }
        }

        /// <summary>Win32 Job Object 互操作。仅本 partial 内使用。</summary>
        private static class NativeJob
        {
            private const int JobObjectExtendedLimitInformation = 9;
            private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IO_COUNTERS
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            // 必须整块 Sequential 布局:x64 对齐下与原生结构完全一致
            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, int cbJobObjectInformationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hObject);

            /// <summary>创建设置了 KILL_ON_JOB_CLOSE 的作业对象并返回句柄;失败返回 IntPtr.Zero。</summary>
            public static IntPtr CreateKillOnCloseJob()
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        ref info,
                        Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }

                return job;
            }

            public static bool AssignProcessToJob(IntPtr job, IntPtr process) => AssignProcessToJobObject(job, process);

            public static void Close(IntPtr handle) => CloseHandle(handle);
        }
    }
}
