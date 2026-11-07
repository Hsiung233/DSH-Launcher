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
    /// “全局装出来”**不限定包管理器**:npm 与 pnpm 都会在各自的全局 bin 目录生成 shim
    /// (Windows 上是 <c>dsh.cmd</c>,类 Unix 上是符号链接/可执行脚本),两者在 PATH 上长得完全一样。
    /// 因此定位只看命令是否可执行,不看它是谁装的;只有"该用哪条命令去安装/更新"才需要
    /// <see cref="DetectInstaller"/>(见 <see cref="DshInstaller"/>)。
    /// </para>
    /// <para>
    /// pnpm 全局安装的 Windows shim(<c>PNPM_HOME\bin\dsh.cmd</c>)不自带 <c>NODE_PATH</c>,
    /// 而全局虚拟库布局下没有它 dsh 就加载不到原生插件 —— <see cref="ResolveNodePathFor"/>
    /// 负责把这个缺口补在子进程环境里(dsh 启动走这条)。
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

        /// <summary>Windows 上能由 <c>call</c> 执行的 shim 扩展名(npm 与 pnpm 都生成 .cmd)。</summary>
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
                PlatformProcess.ShellCommandForExecutable(shimPath, arguments),
                ResolveNodePathFor(shimPath));
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
                    : (string.Empty, $"未找到“{CommandName}”命令。npm / pnpm 的全局 bin 目录可能不在 PATH 中,或包未正确安装。\r\n"
                        + $"{PlatformProcess.LocateCommandLine(CommandName)} 输出:\r\n{probeOutput}");
            }
            catch (Exception ex)
            {
                return (string.Empty, $"定位 dsh 命令失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 定位全局 bin 下的 dsh 命令。
        /// Windows:where dsh → 取 .cmd/.exe(npm 与 pnpm 装出来的都是 dsh.cmd);
        /// 类 Unix(macOS/Linux):command -v dsh → 取第一条(npm/pnpm 装出来的是**无扩展名**的
        /// 符号链接,例如 /usr/local/bin/dsh,按 .cmd/.exe 过滤只会得出“未找到”)。
        /// <para>
        /// PATH 里没有时继续兜底扫描已知的全局 bin 目录(见 <see cref="BuildKnownBinDirectories"/>):
        /// GUI 进程继承的 PATH 常常缺 pnpm 的 <c>PNPM_HOME\bin</c>,而 dsh 明明装着 ——
        /// 原先只认 PATH,表现就是"用 pnpm 装的 dsh 检测不到"。
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
        /// Windows 上 <c>where</c> 可能先列出无扩展名的 sh 脚本(pnpm 与 Git Bash 都会生成),
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
        /// </summary>
        internal static IReadOnlyList<string> BuildKnownBinDirectories(
            bool isWindows,
            string? pnpmHome,
            string? localAppData,
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

            // pnpm:Windows 上 PNPM_HOME 是 HOME 目录(LOCALAPPDATA\pnpm),shim 在它的 bin 子目录;
            // 类 Unix 上 PNPM_HOME 本身就是 shim 目录。两种形态都加,顺序不影响结果。
            Add(pnpmHome is null ? null : Path.Combine(pnpmHome.Trim(), "bin"));
            Add(pnpmHome);

            if (isWindows)
            {
                Add(localAppData is null ? null : Path.Combine(localAppData, "pnpm", "bin"));
                Add(localAppData is null ? null : Path.Combine(localAppData, "pnpm"));
                Add(roamingAppData is null ? null : Path.Combine(roamingAppData, "npm")); // npm 在 Windows 的默认全局 bin
                Add(userProfile is null ? null : Path.Combine(userProfile, "AppData", "Roaming", "npm"));
                Add(programFiles is null ? null : Path.Combine(programFiles, "nodejs"));   // node MSI 安装时的 npm 前缀
                Add(userProfile is null ? null : Path.Combine(userProfile, ".volta", "bin"));
                Add(userProfile is null ? null : Path.Combine(userProfile, "scoop", "shims"));
            }
            else
            {
                Add(home is null ? null : Path.Combine(home, ".local", "share", "pnpm"));
                Add(home is null ? null : Path.Combine(home, "Library", "pnpm"));
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
                Environment.GetEnvironmentVariable("PNPM_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
        /// 读 <c>dsh --version</c> 拿版本号。这是**与安装来源无关**的一手证据:
        /// shim 能从 PATH 或兜底目录找到、能跑起来,就说明包确实可用。
        /// 返回 null 表示没定位到命令、命令执行失败(旧版本不认 --version)或输出里没有版本号,
        /// 调用方应回退到包管理器的全局清单。
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
        /// pnpm 全局安装时要补进子进程的 <c>NODE_PATH</c>;不需要时返回空串(调用方原样不动)。
        /// <para>
        /// 为什么需要:pnpm 12 的全局安装默认启用全局虚拟库(<c>enableGlobalVirtualStore</c>),
        /// 包实体在 <c>&lt;store&gt;\links\...</c> —— 在项目目录之外。pnpm 给项目内
        /// <c>node_modules\.bin\dsh.cmd</c> 注入了 NODE_PATH,却**没给** PATH 上的
        /// <c>PNPM_HOME\bin\dsh.cmd</c> 注,而后者才是 <c>where dsh</c> 命中的。
        /// 结果:dsh 的原生插件加载器按真实路径向上找不到可选平台包
        /// (<c>node-addon-require-builtin-win32-x64-msvc</c>),<c>dsh web</c> 一启动就
        /// <c>host preparation failed: No usable native binding found</c>。
        /// </para>
        /// <para>
        /// 这里按 pnpm 项目内 shim 的口径补齐(本地实测仅补 hoist 目录即可加载成功):
        /// ① 全局项目根 <c>node_modules</c>;② 兜底提升目录 <c>node_modules\.pnpm\node_modules</c>;
        /// ③ dsh 包真实目录下的 <c>node_modules</c> 与它所在的虚拟目录。
        /// 非 pnpm 全局 shim(npm / nodejs 安装)一律返回空串,不改变任何既有行为。
        /// </para>
        /// <para>
        /// 抗升级设计:优先**读 shim 内容**找到它指向的 <c>global\&lt;版本&gt;\&lt;hash&gt;</c> 项目 ——
        /// 路径里的版本目录、hash、甚至 shim 所在目录都没写死,所以 pnpm 升级
        /// (global/v11 → v12、装到新 hash、重修 shim)后下一次启动自动跟着变;
        /// 只有 shim 读不出文本(如 exe)时才退回"目录是 pnpm 全局 bin"+扫描最近项目。
        /// </para>
        /// </summary>
        public static string ResolveNodePathFor(string shimPath)
        {
            var binDir = TryGetDirectory(shimPath);
            if (binDir is null)
            {
                return string.Empty;
            }

            // ① 先看 shim 内容:它指向哪个 pnpm 全局项目,就补哪个项目的目录。
            //    这条与 bin 目录叫什么无关,pnpm 改了全局 bin 布局也能跟上。
            var projectDir = TryReadGlobalProjectFromShim(shimPath, binDir);

            // ② 读不出文本(shim 是 exe、或格式变了)时,才退回“目录是 pnpm 全局 bin”这一前提 + 扫描。
            //    目录不匹配就不动环境:npm / nodejs 装出来的 shim 一律原样。
            if (projectDir is null)
            {
                if (!IsPnpmGlobalBinDirectory(binDir))
                {
                    return string.Empty;
                }

                projectDir = FindNewestGlobalProject(binDir);
            }

            if (projectDir is null)
            {
                return string.Empty;
            }

            var entries = new List<string>();
            var comparison = PlatformProcess.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            void Add(string? dir)
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return;
                }

                try
                {
                    if (!Directory.Exists(dir))
                    {
                        return;
                    }

                    var trimmed = Path.TrimEndingDirectorySeparator(dir);
                    if (entries.Any(existing => string.Equals(existing, trimmed, comparison)))
                    {
                        return;
                    }

                    entries.Add(trimmed);
                }
                catch (Exception)
                {
                    // 单个候选目录不合法不该让整条 NODE_PATH 失效
                }
            }

            Add(Path.Combine(projectDir, "node_modules"));
            Add(Path.Combine(projectDir, "node_modules", ".pnpm", "node_modules"));

            var dshDir = TryResolveRealDirectory(Path.Combine(projectDir, "node_modules", "@deepseek-ai", CommandName));
            if (dshDir is not null)
            {
                Add(Path.Combine(dshDir, "node_modules"));
                Add(Path.GetDirectoryName(Path.GetDirectoryName(dshDir)));
            }

            if (entries.Count == 0)
            {
                return string.Empty;
            }

            // 与 pnpm 的 shim 一致:新值在前,保留调用方已有的 NODE_PATH
            var existing = Environment.GetEnvironmentVariable("NODE_PATH");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                entries.Add(existing.Trim());
            }

            return string.Join(Path.PathSeparator, entries);
        }

        /// <summary>shim 所在目录;取不到返回 null。</summary>
        private static string? TryGetDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                var dir = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(dir) ? null : Path.TrimEndingDirectorySeparator(dir);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>该目录是否是 pnpm 的**全局 bin** 目录(只有这类 shim 存在 NODE_PATH 缺口)。</summary>
        private static bool IsPnpmGlobalBinDirectory(string directory)
        {
            var comparison = PlatformProcess.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var normalized = Path.TrimEndingDirectorySeparator(directory);
            return PnpmGlobalBinDirectories().Any(dir => string.Equals(dir, normalized, comparison));
        }

        /// <summary>pnpm 可能用来放全局 shim 的目录(Windows 是 PNPM_HOME\bin,类 Unix 就是 PNPM_HOME 本身)。</summary>
        private static IEnumerable<string> PnpmGlobalBinDirectories()
        {
            var pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");
            var localAppData = PlatformProcess.LocalAppDataDirectory;
            var home = Environment.GetEnvironmentVariable("HOME");

            var candidates = new List<string?>();
            if (!string.IsNullOrWhiteSpace(pnpmHome))
            {
                candidates.Add(Path.Combine(pnpmHome.Trim(), "bin"));
                candidates.Add(pnpmHome.Trim()); // 类 Unix 上 PNPM_HOME 本身就是 shim 目录
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "pnpm", "bin"));
            }

            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(Path.Combine(home, ".local", "share", "pnpm"));
                candidates.Add(Path.Combine(home, "Library", "pnpm"));
            }

            return candidates
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(dir => Path.TrimEndingDirectorySeparator(dir!));
        }

        /// <summary>
        /// 从 shim 内容里读出它指向的 pnpm 全局项目目录(<c>&lt;PNPM_HOME&gt;\global\&lt;v&gt;\&lt;hash&gt;</c>)。
        /// Windows 的 <c>.cmd</c> 与类 Unix 的 sh shim 都是纯文本,里面带着相对/绝对目标路径;
        /// 单个 <c>.exe</c> shim 读不出文本时返回 null,由扫描兜底。
        /// </summary>
        private static string? TryReadGlobalProjectFromShim(string shimPath, string binDir)
        {
            try
            {
                var info = new FileInfo(shimPath);
                if (!info.Exists || info.Length == 0 || info.Length > 64 * 1024)
                {
                    return null;
                }

                var match = GlobalProjectRegex().Match(File.ReadAllText(shimPath));
                if (!match.Success)
                {
                    return null;
                }

                var relative = match.Value;
                var candidates = new[]
                {
                    Path.GetFullPath(Path.Combine(binDir, "..", relative)), // Windows: PNPM_HOME\bin\..\global\...
                    Path.GetFullPath(Path.Combine(binDir, relative)),        // 类 Unix: PNPM_HOME\global\...
                };

                return candidates.FirstOrDefault(HasDshPackage);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 兜底:扫 <c>&lt;PNPM_HOME&gt;\global\&lt;v&gt;\*</c> 找带 dsh 的全局项目(取最近写入的一个)。
        /// 只在 shim 读不出目标时用,例如 shim 是 exe 或格式变了。
        /// </summary>
        private static string? FindNewestGlobalProject(string binDir)
        {
            try
            {
                var globalRoot = Path.GetFullPath(Path.Combine(binDir, "..", "global"));
                if (!Directory.Exists(globalRoot))
                {
                    return null;
                }

                var projects = Directory.EnumerateDirectories(globalRoot)
                    .SelectMany(versionDir => Directory.EnumerateDirectories(versionDir))
                    .Where(HasDshPackage)
                    .Select(dir => new DirectoryInfo(dir))
                    .ToList();

                return projects
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .FirstOrDefault()
                    ?.FullName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>项目目录下是否装着 dsh(<c>node_modules\@deepseek-ai\dsh</c>)。</summary>
        private static bool HasDshPackage(string projectDir)
        {
            try
            {
                return Directory.Exists(Path.Combine(projectDir, "node_modules", "@deepseek-ai", CommandName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>解析符号链接到最终目录(links 布局下的包实体);失败时返回 null。</summary>
        private static string? TryResolveRealDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return null;
                }

                var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
                var resolved = target?.FullName ?? path;
                return Directory.Exists(resolved) ? Path.TrimEndingDirectorySeparator(resolved) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从 <c>npm ls -g</c> / <c>pnpm ls -g</c> 的树形输出里取 <c>&lt;包名&gt;@版本</c>。
        /// 两种管理器输出同形(<c>└── @deepseek-ai/dsh@0.2.1</c>),只有调用命令不同。
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

        /// <summary>
        /// 判定 shim 是哪个包管理器装的(决定安装/更新走 npm 还是 pnpm)。
        /// 依据是 shim 所在目录:npm 的全局 bin 与 pnpm 的全局 bin 是不同目录,
        /// 且都不与对方重叠 —— 比"两个管理器都问一遍"少起子进程。
        /// </summary>
        public static DshInstaller DetectInstaller(string? shimPath)
            => DetectInstaller(shimPath, KnownPnpmDirectories());

        /// <summary>
        /// 同上,但 pnpm 的全局 bin 目录由调用方给出(便于单元测试)。
        /// </summary>
        internal static DshInstaller DetectInstaller(string? shimPath, IEnumerable<string> pnpmBinDirectories)
        {
            if (string.IsNullOrWhiteSpace(shimPath))
            {
                return DshInstaller.Unknown;
            }

            string? directory;
            try
            {
                directory = Path.GetDirectoryName(shimPath);
            }
            catch (Exception)
            {
                return DshInstaller.Unknown;
            }

            if (string.IsNullOrEmpty(directory))
            {
                return DshInstaller.Unknown;
            }

            var normalized = Path.TrimEndingDirectorySeparator(directory);
            var comparison = PlatformProcess.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var pnpmDirs = pnpmBinDirectories
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(dir => Path.TrimEndingDirectorySeparator(dir))
                .ToList();

            if (pnpmDirs.Any(dir => string.Equals(dir, normalized, comparison)))
            {
                return DshInstaller.Pnpm;
            }

            // 精确比对不中时按路径段推断(自定义 PNPM_HOME、~/.local/share/pnpm、nvm/volta 布局等)
            var bySegments = DetectBySegments(normalized);
            if (bySegments != DshInstaller.Unknown)
            {
                return bySegments;
            }

            // 类 Unix 上全局 bin 里放的是符号链接:npm 指向 lib/node_modules,
            // pnpm 指向 global/<n>/node_modules/.pnpm/... —— 解析后那段 .pnpm 才是铁证
            var resolved = TryResolveLinkDirectory(shimPath);
            return resolved is null ? DshInstaller.Unknown : DetectBySegments(resolved);
        }

        /// <summary>当前机器上 pnpm 可能使用的全局 bin 目录(供 <see cref="DetectInstaller(string?)"/> 精确比对)。</summary>
        private static IEnumerable<string> KnownPnpmDirectories()
        {
            var pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");
            var localAppData = PlatformProcess.LocalAppDataDirectory;

            var candidates = new[]
            {
                pnpmHome is null ? null : Path.Combine(pnpmHome.Trim(), "bin"),
                pnpmHome,
                string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "pnpm", "bin"),
                string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "pnpm"),
                Environment.GetEnvironmentVariable("HOME") is string home && home.Length > 0
                    ? Path.Combine(home, ".local", "share", "pnpm")
                    : null,
            };

            return candidates
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(dir => dir!);
        }

        /// <summary>按路径段判断安装来源;顺序上 pnpm 优先(pnpm 的目录名里不会出现独立的 npm 段)。</summary>
        private static DshInstaller DetectBySegments(string directory)
        {
            var segments = directory
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.TrimStart('.'))
                .ToList();

            if (segments.Any(segment => segment.Equals("pnpm", StringComparison.OrdinalIgnoreCase)
                                        || segment.Equals("pnpm-global", StringComparison.OrdinalIgnoreCase)))
            {
                return DshInstaller.Pnpm;
            }

            return segments.Any(segment => segment.Equals("npm", StringComparison.OrdinalIgnoreCase)
                                           || segment.Equals("npm-global", StringComparison.OrdinalIgnoreCase)
                                           || segment.Equals("nodejs", StringComparison.OrdinalIgnoreCase))
                ? DshInstaller.Npm
                : DshInstaller.Unknown;
        }

        /// <summary>解析符号链接的最终目标目录;不是链接、解析失败或目标不存在时返回 null。</summary>
        private static string? TryResolveLinkDirectory(string path)
        {
            try
            {
                var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
                if (target is null)
                {
                    return null;
                }

                var targetPath = target.FullName;
                if (Directory.Exists(targetPath))
                {
                    return targetPath;
                }

                return Path.GetDirectoryName(targetPath);
            }
            catch (Exception)
            {
                // 链接不可解析/目标缺失都只是"推断失败",不影响已定位到的 shim 本身
                return null;
            }
        }

        [GeneratedRegex(@"^\s*v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)*(?:\+[0-9A-Za-z.]+)*)\s*$", RegexOptions.CultureInvariant, "zh-CN")]
        private static partial Regex VersionLineRegex();

        /// <summary>pnpm 全局项目目录:&lt;PNPM_HOME&gt;\global\&lt;版本&gt;\&lt;hash&gt;。</summary>
        [GeneratedRegex(@"(?i)global(?:\\|/)v\d+(?:\\|/)[0-9a-f-]+", RegexOptions.CultureInvariant)]
        private static partial Regex GlobalProjectRegex();
    }
}
