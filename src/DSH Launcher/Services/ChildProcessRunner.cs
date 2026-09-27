using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 子进程执行的**唯一出口**:定位/编码/环境注入/等待退出这些共性只在这里写一遍。
    /// <para>
    /// 为什么要有它:改造前同一套"静默启动 → 收 stdio → 逐行判定编码 → 等退出"的管道被抄了五份
    /// (npm 安装、npm 查询、dsh 子命令捕获、dsh 子命令流式、pnpm 探测),抄漏的表现已经出现过 ——
    /// 其中一份既没有 <c>Dispose</c> 也没有收尾的 <c>WaitForExit</c>,末尾输出会被截断。
    /// </para>
    /// <para>
    /// 统一注入"环境设置"(npm 源 / 代理,见 <see cref="ChildEnvironment"/>):
    /// 本类服务的都是**非 <c>dsh web</c>** 的子进程(npm 安装/查询、命令探测、<c>dsh plugin</c>)。
    /// <c>dsh web</c> 服务进程**不**走本类 —— 它是本机服务,注入代理会让 WebView/浏览器访问
    /// <c>127.0.0.1</c> 绕一圈,那条路径在 <see cref="DshService"/> 里单独拼启动参数。
    /// </para>
    /// <para>
    /// 输出一律按"原样字节"收下再逐行判定编码(Node 系工具写 UTF-8,cmd.exe 自身消息是 OEM 代码页,
    /// 两者会落在同一管道上,见 <see cref="PlatformProcess.DecodeChildOutputLine"/>)。调用方不必选:
    /// 这条路径上的输出要么给人看、要么被解析,两种情况都需要正确解码。
    /// </para>
    /// </summary>
    internal static class ChildProcessRunner
    {
        /// <summary>捕获模式的结果:退出码 + 已解码的 stdout/stderr。</summary>
        /// <param name="ExitCode">子进程退出码。</param>
        /// <param name="Stdout">标准输出(已逐行解码)。</param>
        /// <param name="Stderr">标准错误(已逐行解码)。</param>
        internal readonly record struct CaptureResult(int ExitCode, string Stdout, string Stderr);

        /// <summary>
        /// 静默执行一条命令行并捕获输出(不抛异常之外的任何副作用;失败由调用方决定怎么上报)。
        /// </summary>
        public static async Task<CaptureResult> CaptureAsync(string command)
        {
            using var process = StartShell(command);

            // 必须先 Start 再读 StandardOutput,否则抛
            // "StandardOut has not been redirected or the process hasn't started yet"
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new CaptureResult(
                process.ExitCode,
                PlatformProcess.DecodeChildOutputText(await stdout),
                PlatformProcess.DecodeChildOutputText(await stderr));
        }

        /// <summary>
        /// 执行一条命令行并把输出**逐行**回调(用于需要实时反馈的操作,如安装/卸载插件);
        /// 返回退出码。回调可能在线程池线程上触发。
        /// </summary>
        public static async Task<int> StreamAsync(string command, Action<string> onLine)
        {
            using var process = StartShell(command);
            process.EnableRaisingEvents = true;

            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onLine(PlatformProcess.DecodeChildOutputLine(e.Data));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onLine(PlatformProcess.DecodeChildOutputLine(e.Data));
                }
            };
            process.Exited += (_, _) => exited.TrySetResult();

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await exited.Task;

            // ⚠ 无参 WaitForExit 会等待异步输出读取**全部完成**,不能省:
            //   否则 pnpm/npm 末尾那几行(通常正是错误原因)会被截断。
            await Task.Run(process.WaitForExit);
            return process.ExitCode;
        }

        /// <summary>按平台 shell 规则启动一个隐藏的子进程(Windows 走 cmd.exe /c,类 Unix 走登录 shell)。</summary>
        private static Process StartShell(string command)
        {
            var startInfo = PlatformProcess.CreateShellStartInfo(
                command, redirectOutput: true, rawByteOutput: true);

            // 统一注入 npm 源 / 代理:这里是所有非 dsh web 子进程的公共出口,
            // 所以只需在这一处注入,不必在每个调用点重复
            ChildEnvironment.Apply(startInfo);

            return Process.Start(startInfo)!;
        }
    }
}
