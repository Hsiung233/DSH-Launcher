using System;
using System.Linq;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// dsh 命令行入口:定位 npm 全局装出来的命令,并执行 dsh 子命令。
    /// <para>
    /// 为什么单独成类:定位与执行既不属于"服务生命周期"(<see cref="DshService"/> 的启停/版本管理),
    /// 也不属于插件管理 —— 它是两端共用的基础设施。抽出来之后依赖方向变成
    /// <c>DshService → DshCli</c>、<c>PluginService → DshCli</c>,
    /// 插件页不再需要为了跑一条 <c>dsh plugin</c> 而依赖整个 DshService。
    /// </para>
    /// <para>
    /// 输出一律经 <see cref="ChildProcessRunner"/>(按原样字节收下再逐行判定编码、
    /// 并注入 npm 源/代理)。
    /// </para>
    /// </summary>
    internal static class DshCli
    {
        /// <summary>命令名(npm 全局 bin 下就是这个名字的 shim)。</summary>
        internal const string CommandName = "dsh";

        /// <summary>
        /// 执行一条 dsh 子命令并捕获输出。
        /// 返回码 -1 表示未能定位 dsh 命令(原因见 <c>Stderr</c>,可直接展示给用户)。
        /// </summary>
        public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(string arguments)
        {
            var (shimPath, locateError) = await TryLocateAsync();
            if (shimPath.Length == 0)
            {
                return (-1, string.Empty, locateError);
            }

            var result = await ChildProcessRunner.CaptureAsync(
                PlatformProcess.ShellCommandForExecutable(shimPath, arguments));
            return (result.ExitCode, result.Stdout, result.Stderr);
        }

        /// <summary>
        /// 执行一条 dsh 子命令,输出按行流式回传(用于需要实时反馈的操作,如安装/卸载插件)。
        /// 返回码 -1 表示未能定位 dsh 命令。
        /// </summary>
        public static async Task<int> RunStreamingAsync(string arguments, Action<string> onOutput)
        {
            var (shimPath, locateError) = await TryLocateAsync();
            if (shimPath.Length == 0)
            {
                onOutput(locateError);
                return -1;
            }

            return await ChildProcessRunner.StreamAsync(
                PlatformProcess.ShellCommandForExecutable(shimPath, arguments), onOutput);
        }

        /// <summary>定位 dsh 命令;失败时返回空串与可直接展示的错误说明。</summary>
        public static async Task<(string ShimPath, string Error)> TryLocateAsync()
        {
            try
            {
                var (found, probeOutput) = await FindAsync();
                return found.Length > 0
                    ? (found, string.Empty)
                    : (string.Empty, $"未找到“{CommandName}”命令。npm 全局 bin 目录可能不在 PATH 中,或包未正确安装。\r\n"
                        + $"{PlatformProcess.LocateCommandLine(CommandName)} 输出:\r\n{probeOutput}");
            }
            catch (Exception ex)
            {
                return (string.Empty, $"定位 dsh 命令失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 定位 npm 全局 bin 下的 dsh 命令。
        /// Windows:where dsh → 取 .cmd/.exe(npm 装出来的是 dsh.cmd);
        /// 类 Unix(macOS/Linux):command -v dsh → 取第一条(npm 装出来的是**无扩展名**的
        /// 符号链接,例如 /usr/local/bin/dsh,按 .cmd/.exe 过滤只会得出“未找到”)。
        /// 返回空串表示未找到,同时返回探测命令的原始输出以便在错误信息里展示。
        /// </summary>
        public static async Task<(string ShimPath, string ProbeOutput)> FindAsync()
        {
            var probeCommand = PlatformProcess.LocateCommandLine(CommandName);
            var probe = await ChildProcessRunner.CaptureAsync(probeCommand);

            var candidates = probe.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);

            var shimPath = PlatformProcess.IsWindows
                ? candidates.FirstOrDefault(line => line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                                    || line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? string.Empty
                : candidates.FirstOrDefault() ?? string.Empty;

            return (shimPath, (probe.Stdout + probe.Stderr).TrimEnd());
        }
    }
}
