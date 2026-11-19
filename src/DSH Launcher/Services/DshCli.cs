using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// dsh 命令行入口:定位全局装出来的命令,并执行 dsh 子命令。
    /// <para>
    /// 为什么单独成类:定位与执行不属于"服务生命周期"(<see cref="DshService"/> 的启停/版本管理),
    /// 而是可独立测试的基础设施。抽出来之后依赖方向纯化为 <c>DshService → DshCli</c>,
    /// 服务层的启停、版本查询与诊断都共用同一套定位/执行逻辑。
    /// </para>
    /// <para>
    /// 只支持 <b>npm 全局安装</b>这一种来源(Windows 上是 <c>%APPDATA%\npm\dsh.cmd</c>,
    /// 类 Unix 上是 <c>~/.npm-global/bin/dsh</c> 之类的符号链接)。定位只看命令是否可执行:
    /// shim 在 PATH 或已知全局 bin 目录里、能跑起来,就认为包可用。
    /// </para>
    /// <para>
    /// 曾经支持的 pnpm 全局安装已整体移除:pnpm 12 的全局虚拟库把包实体放在 store 的
    /// <c>links\</c> 下,PATH 上的 shim 又不带 <c>NODE_PATH</c>,启动器必须反推项目目录补环境;
    /// 而 ESM 解析走真实路径,<c>NODE_PATH</c> 对 <c>import</c> 无效 —— 补了也救不了缺依赖的包
    /// (dsh-web 0.2.2-rc.5 漏声明 zod 就是活例)。维护成本远高于收益。
    /// </para>
    /// <para>
    /// 输出一律经 <see cref="ChildProcessRunner"/>(按原样字节收下再逐行判定编码、
    /// 并注入 npm 源/代理)。
    /// </para>
    /// </summary>
    internal static partial class DshCli
    {
        /// <summary>命令名(全局 bin 下就是这个名字的 shim)。</summary>
        internal const string CommandName = "dsh";

        /// <summary>Windows 上能由 <c>call</c> 执行的 shim 扩展名(npm 全局安装生成 <c>dsh.cmd</c>)。</summary>
        private static readonly string[] WindowsShimNames = { "dsh.cmd", "dsh.exe" };

        /// <summary>执行一条 dsh 子命令并捕获输出。</summary>
        /// <remarks>返回码 -1 表示未能定位 dsh 命令(原因见 <c>Stderr</c>,可直接展示给用户)。</remarks>
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

        /// <summary>定位 dsh 命令;失败时返回空串与可直接展示的错误说明。</summary>
        public static async Task<(string ShimPath, string Error)> TryLocateAsync()
        {
            try
            {
                var (found, probeOutput) = await FindAsync();
                return found.Length > 0
                    ? (found, string.Empty)
                    : (string.Empty, $"未找到“{CommandName}”命令。npm 的全局 bin 目录可能不在 PATH 中,或包未正确安装。\r\n"
                        + $"{PlatformProcess.LocateCommandLine(CommandName)} 输出:\r\n{probeOutput}");
            }
            catch (Exception ex)
            {
                return (string.Empty, $"定位 dsh 命令失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 定位全局 bin 下的 dsh 命令。
        /// Windows:where dsh → 取 .cmd/.exe(npm 全局安装生成的是 dsh.cmd);
        /// 类 Unix(macOS/Linux):command -v dsh → 取第一条(npm 全局安装放的是**无扩展名**的
        /// 符号链接,例如 /usr/local/bin/dsh,按 .cmd/.exe 过滤只会得出“未找到”)。
        /// <para>
        /// PATH 里没有时继续兜底扫描已知的全局 bin 目录(见 <see cref="BuildKnownBinDirectories"/>):
        /// GUI 进程继承的 PATH 常常缺 <c>%APPDATA%\npm</c>,而 dsh 明明装着 ——
        /// 原先只认 PATH,表现就是"装了却检测不到"。
        /// </para>
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

            var shimPath = PickShim(candidates);
            var probeText = (probe.Stdout + probe.Stderr).TrimEnd();
            if (shimPath.Length > 0)
            {
                return (shimPath, probeText);
            }

            var fallback = FindInKnownDirectories();
            return fallback.Length > 0
                ? (fallback, $"{probeText}\r\n(PATH 中未找到,已改用兜底目录命中: {fallback})".TrimStart())
                : (string.Empty, probeText);
        }

        /// <summary>
        /// 从 <c>where dsh</c> / <c>command -v dsh</c> 的输出里挑出可执行的那一条。
        /// <para>
        /// Windows 上 <c>where</c> 可能先列出无扩展名的 sh 脚本(Git Bash 之类会生成),
        /// 但 <c>call dsh</c> 只认 .cmd/.bat/exe;这时改取同目录的 <c>dsh.cmd</c>。
        /// </para>
        /// </summary>
        private static string PickShim(IEnumerable<string> candidates)
        {
            var list = candidates.ToList();
            if (!PlatformProcess.IsWindows)
            {
                return list.FirstOrDefault() ?? string.Empty;
            }

            var preferred = list.FirstOrDefault(line =>
                line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }

            var bareScript = list.FirstOrDefault(line => Path.GetExtension(line).Length == 0);
            return bareScript is not null && File.Exists(bareScript + ".cmd")
                ? bareScript + ".cmd"
                : string.Empty;
        }

        /// <summary>
        /// PATH 探测失败后兜底扫描的全局 bin 目录(按优先级,已去重)。
        /// 纯函数:所有输入由调用方给出,便于单元测试固定每个平台的候选表与顺序。
        /// <para>只列 npm 与同类 Node 版本管理器的 bin 目录 —— pnpm 全局安装已不再支持。</para>
        /// </summary>
        internal static IReadOnlyList<string> BuildKnownBinDirectories(
            bool isWindows,
            string? roamingAppData,
            string? programFiles,
            string? userProfile,
            string? home)
        {
            var dirs = new List<string>();
            var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            void Add(string? dir)
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return;
                }

                var trimmed = Path.TrimEndingDirectorySeparator(dir.Trim());
                if (trimmed.Length == 0 || dirs.Any(existing => string.Equals(existing, trimmed, comparison)))
                {
                    return;
                }

                dirs.Add(trimmed);
            }

            if (isWindows)
            {
                Add(roamingAppData is null ? null : Path.Combine(roamingAppData, "npm")); // npm 在 Windows 的默认全局 bin
                Add(userProfile is null ? null : Path.Combine(userProfile, "AppData", "Roaming", "npm"));
                Add(programFiles is null ? null : Path.Combine(programFiles, "nodejs"));   // node MSI 安装时的 npm 前缀
                Add(userProfile is null ? null : Path.Combine(userProfile, ".volta", "bin"));
                Add(userProfile is null ? null : Path.Combine(userProfile, "scoop", "shims"));
            }
            else
            {
                Add(home is null ? null : Path.Combine(home, ".local", "bin"));
                Add(home is null ? null : Path.Combine(home, ".npm-global", "bin"));
                Add(home is null ? null : Path.Combine(home, ".volta", "bin"));
                Add("/opt/homebrew/bin");
                Add("/usr/local/bin");
                Add("/usr/bin");
                Add("/bin");
            }

            return dirs;
        }

        /// <summary>当前机器上的兜底目录表(启动时求值一次,不随 PATH 变化)。</summary>
        private static readonly Lazy<IReadOnlyList<string>> KnownBinDirectories = new(() =>
            BuildKnownBinDirectories(
                PlatformProcess.IsWindows,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("HOME")));

        /// <summary>在兜底目录里逐个找可执行的 shim;命中返回完整路径,否则空串。</summary>
        private static string FindInKnownDirectories()
        {
            foreach (var dir in KnownBinDirectories.Value)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    if (PlatformProcess.IsWindows)
                    {
                        foreach (var name in WindowsShimNames)
                        {
                            var candidate = Path.Combine(dir, name);
                            if (File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }

                        continue;
                    }

                    var unixCandidate = Path.Combine(dir, CommandName);
                    if (File.Exists(unixCandidate))
                    {
                        return unixCandidate;
                    }
                }
                catch (Exception)
                {
                    // 单个目录不可读(权限、畸形路径)不该打断整张兜底表
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 读 <c>dsh --version</c> 拿版本号。这是**与安装方式无关**的一手证据:
        /// shim 能从 PATH 或兜底目录找到、能跑起来,就说明包确实可用。
        /// 返回 null 表示没定位到命令、命令执行失败(旧版本不认 --version)或输出里没有版本号,
        /// 调用方应回退到 <c>npm ls -g</c> 的全局清单。
        /// </summary>
        public static async Task<string?> TryGetVersionAsync(string shimPath)
        {
            if (shimPath.Length == 0 || !File.Exists(shimPath))
            {
                return null;
            }

            try
            {
                var result = await ChildProcessRunner.CaptureAsync(
                    PlatformProcess.ShellCommandForExecutable(shimPath, "--version"));
                return result.ExitCode == 0 ? ParseVersionOutput(result.Stdout) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从 <c>dsh --version</c> 的输出里取版本号(首行为 <c>0.2.1-rc.4</c> 或带 <c>v</c> 前缀)。
        /// 只认"整行就是一个版本号"的行:错误信息里常夹着 Node 的版本(<c>Node.js v20.11.0</c>),
        /// 宽松匹配会把它当成 dsh 版本。
        /// </summary>
        internal static string? ParseVersionOutput(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            foreach (var line in output.Split('\n'))
            {
                var match = VersionLineRegex().Match(line.Trim());
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            return null;
        }

        /// <summary>
        /// 从 <c>npm ls -g</c> 的树形输出里取 <c>&lt;包名&gt;@版本</c>
        /// (形如 <c>└── @deepseek-ai/dsh@0.2.1</c>)。
        /// </summary>
        internal static string? ParseGlobalListVersion(string? output, string packageName)
        {
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(packageName))
            {
                return null;
            }

            // 包名后面必须紧跟 @:否则 @deepseek-ai/dsh-hmr@1.0.0 也会命中
            var match = Regex.Match(
                output,
                Regex.Escape(packageName) + @"@(\d[0-9A-Za-z.+-]*)",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        [GeneratedRegex(@"^\s*v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)*(?:\+[0-9A-Za-z.]+)*)\s*$", RegexOptions.CultureInvariant, "zh-CN")]
        private static partial Regex VersionLineRegex();
    }
}
