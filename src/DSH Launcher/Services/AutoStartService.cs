using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace DSH_Launcher.Services
{
    /// <summary>开机自启动在系统侧的注册状态。</summary>
    public enum AutoStartState
    {
        /// <summary>当前平台或当前运行方式无法注册(例如以 <c>dotnet</c> 主机启动时拿不到程序本体路径)。</summary>
        Unsupported = 0,

        /// <summary>系统侧没有自启动项。</summary>
        NotRegistered = 1,

        /// <summary>系统侧有自启动项,且指向当前程序。</summary>
        Registered = 2,

        /// <summary>系统侧有自启动项,但指向别的路径(程序被移动过、或旧安装残留)。</summary>
        RegisteredOtherPath = 3,
    }

    /// <summary>
    /// 一次系统侧读取的结果。<paramref name="RegisteredTarget"/> 是**自启动项当前指向的可执行文件路径**
    /// (Windows 能从值里解析出来;其他平台只看文件在不在、内容里有没有当前路径,故为 null)。
    /// </summary>
    public readonly record struct AutoStartStatus(AutoStartState State, string? RegisteredTarget);

    /// <summary>“设置”与“系统状态”对齐时该对系统侧做的动作(见 <see cref="AutoStartService.Decide"/>)。</summary>
    internal enum AutoStartAction
    {
        /// <summary>已经一致,什么都不用做。</summary>
        None = 0,

        /// <summary>写入/更新自启动项,使其指向当前程序。</summary>
        Write = 1,

        /// <summary>删除系统里残留的自启动项。</summary>
        Delete = 2,

        /// <summary>设置开着但当前运行方式注册不了 —— 无从下手,只能告诉用户。</summary>
        Unsupported = 3,
    }

    /// <summary>
    /// 开机自启动(单例):读写系统侧的自启动项,并让它与设置保持一致。
    /// <para>
    /// 分工:**系统侧**由本服务负责,`AppSettings.AutoStartOnLogon` 由设置页负责写。
    /// 写系统失败时**不能**把设置改掉(否则设置说“已开启”、系统里其实没有,用户下次登录才发现没生效),
    /// 所以 <see cref="TrySet"/> 只报成败与原因,由调用方决定怎么处置设置值。
    /// </para>
    /// <para>
    /// 本服务**不写日志**:它比 <c>DshService</c> 更底层,日志由调用方转写
    /// (<see cref="Reconcile"/> 把话交给 App 启动流程,与 <c>SettingsService.TakeLoadDiagnostic</c> 同一路数)。
    /// </para>
    /// </summary>
    public sealed class AutoStartService
    {
        /// <summary>拿不到程序本体路径时的统一说法(界面提示与日志共用一份文案)。</summary>
        public const string UnsupportedReason = "当前运行方式无法注册开机自启动(需以可执行文件本体启动)";

        public static AutoStartService Instance { get; } = new();

        private AutoStartService()
        {
        }

        /// <summary>当前平台是否支持注册开机自启动。</summary>
        private static bool IsSupported
            => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

        /// <summary>
        /// 自启动项要指向的可执行文件路径;取不到时返回 null。
        /// <para>
        /// ⚠ 不能无条件用 <see cref="Environment.ProcessPath"/>:在 <c>dotnet run</c> / <c>dotnet exec</c> 下
        /// 它是 <c>dotnet.exe</c> 主机本身 —— 把它写进自启动项,登录后只会拉起一个空主机(还可能弹个控制台)。
        /// 与其注册一个错的,不如明说当前运行方式不支持。
        /// </para>
        /// </summary>
        public static string? ResolveExecutablePath() => ResolveExecutablePath(Environment.ProcessPath);

        /// <summary>纯逻辑部分(进程路径由调用方给,便于单元测试)。</summary>
        internal static string? ResolveExecutablePath(string? processPath)
        {
            if (string.IsNullOrEmpty(processPath))
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(processPath);
            return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ? null : processPath;
        }

        /// <summary>读取系统侧的自启动状态(读不到时按“未注册”处理,真正的失败原因留到写入时给出)。</summary>
        public AutoStartStatus Read()
        {
            var executable = ResolveExecutablePath();
            if (!IsSupported || executable is null)
            {
                return new AutoStartStatus(AutoStartState.Unsupported, null);
            }

            try
            {
                // ⚠ 写成 if 而不是三目:平台兼容性分析器只认这些标准守卫写法(见 ReadWindows 上的
                // [SupportedOSPlatform]),换个写法就会报 CA1416。
                if (OperatingSystem.IsWindows())
                {
                    return ReadWindows(executable);
                }

                return ReadEntryFile(executable);
            }
            catch (Exception)
            {
                // 注册表被策略锁住、文件读不出来等:按“未注册”展示即可 ——
                // 界面上用户仍可尝试开启,那一步会给出具体原因。
                return new AutoStartStatus(AutoStartState.NotRegistered, null);
            }
        }

        /// <summary>
        /// 写入(或删除)系统自启动项。失败时返回 false 并给出可直接展示给用户的原因。
        /// 幂等:已是目标状态时再写一遍也没有副作用。
        /// </summary>
        public bool TrySet(bool enabled, out string? failure)
        {
            failure = null;

            var executable = ResolveExecutablePath();
            if (!IsSupported || executable is null)
            {
                failure = UnsupportedReason;
                return false;
            }

            try
            {
                // 与 Read 同理:平台守卫必须写成 if,分析器才认(否则 CA1416)
                if (OperatingSystem.IsWindows())
                {
                    failure = WriteWindows(enabled, executable);
                }
                else
                {
                    failure = WriteEntryFile(enabled, executable);
                }
            }
            catch (Exception ex)
            {
                failure = DescribeFailure(ex);
            }

            return failure is null;
        }

        /// <summary>
        /// 让系统自启动项与设置保持一致,应用启动时调用一次:
        /// <list type="bullet">
        /// <item>设置开着但系统里没有(或指向旧路径,如程序换了安装目录)→ 补写/更新;</item>
        /// <item>设置关着但系统里还留着 → 删掉(设置是唯一真相,否则关掉后下次登录照样被拉起)。</item>
        /// </list>
        /// <para>
        /// ⚠ 只在**确有差异**时才写系统:每次启动都无条件重写会让注册表监控、安全软件平白报警,
        /// 而这里要修的只是“程序被移动/注册项被手工删掉”这类不一致。
        /// </para>
        /// </summary>
        /// <returns>需要让用户知道的一句话(null = 一切正常,无需报告)。</returns>
        public string? Reconcile()
        {
            var enabled = SettingsService.Instance.Settings.AutoStartOnLogon;
            var status = this.Read();

            switch (Decide(enabled, status.State))
            {
                case AutoStartAction.Unsupported:
                    return $"[启动] 开机自启动:设置已开启,但{UnsupportedReason}";

                case AutoStartAction.Write:
                    if (!this.TrySet(true, out var writeFailure))
                    {
                        return $"[启动] 开机自启动:按设置写入自启动项失败:{writeFailure}";
                    }

                    return status.State == AutoStartState.RegisteredOtherPath
                        ? $"[启动] 开机自启动:自启动项原先指向 {status.RegisteredTarget ?? "其它路径"},已更新为当前程序"
                        : "[启动] 开机自启动:已按设置补写自启动项";

                case AutoStartAction.Delete:
                    if (!this.TrySet(false, out var deleteFailure))
                    {
                        return $"[启动] 开机自启动:按设置清除自启动项失败:{deleteFailure}";
                    }

                    return "[启动] 开机自启动:设置已关闭,已清除系统里残留的自启动项";

                default:
                    return null;
            }
        }

        /// <summary>
        /// “设置 × 系统状态”的**决策表**(纯函数,被单元测试逐一钉住)。
        /// <para>
        /// 为什么要抽出来:第一版把“指向别处的旧注册项”和“系统里没有”当成了同一回事
        /// (<c>enabled == (state == Registered)</c>),于是**设置关着时残留的旧自启动项永远清不掉** ——
        /// 它只在“程序被移动过”的真实注册表上才暴露(靠验证脚本跑出来,读代码看不出来)。
        /// 四种组合值得单独成表逐条固定。
        /// </para>
        /// </summary>
        internal static AutoStartAction Decide(bool enabled, AutoStartState state)
        {
            if (state == AutoStartState.Unsupported)
            {
                return enabled ? AutoStartAction.Unsupported : AutoStartAction.None;
            }

            if (enabled)
            {
                // 已指向当前程序就什么都不做;缺失、或指向旧路径(程序换了安装目录)都要写入/更新
                return state == AutoStartState.Registered ? AutoStartAction.None : AutoStartAction.Write;
            }

            // 关闭时:系统里只要还留着东西(不管它指向哪)就得清掉,否则下次登录照样会被拉起
            return state == AutoStartState.NotRegistered ? AutoStartAction.None : AutoStartAction.Delete;
        }

        /// <summary>
        /// 设置页那一行下方的系统侧状态提示(纯函数,便于单元测试)。
        /// 文案要说清“设置与系统是否一致、不一致会怎么自愈”,否则用户看到开关开着而提示写着“未注册”只会困惑。
        /// </summary>
        public static string DescribeHint(AutoStartStatus status, bool enabled)
        {
            switch (status.State)
            {
                case AutoStartState.Registered:
                    return enabled
                        ? "系统自启动项:已注册,指向当前程序"
                        : "系统自启动项:仍在(本项已关闭),下次启动应用时会清除";

                case AutoStartState.RegisteredOtherPath:
                    var where = status.RegisteredTarget ?? "其它路径";
                    return enabled
                        ? $"系统自启动项:指向 {where}(不是当前程序),下次启动应用时会更新"
                        : $"系统自启动项:仍在,指向 {where}(本项已关闭),下次启动应用时会清除";

                case AutoStartState.NotRegistered:
                    return enabled
                        ? "系统自启动项:未注册,下次启动应用时会补写"
                        : "系统自启动项:未注册";

                default:
                    return UnsupportedReason;
            }
        }

        /// <summary>
        /// 系统侧自启动项的**位置与内容**(设置页提示行的 tooltip)。
        /// 它也是排查时最直接的证据:自启动没生效时,先看这里指的程序对不对。
        /// </summary>
        public string DescribeRegistration()
        {
            var executable = ResolveExecutablePath();
            if (!IsSupported || executable is null)
            {
                return UnsupportedReason;
            }

            if (OperatingSystem.IsWindows())
            {
                return $@"注册表 HKCU\{AutoStartEntry.WindowsRunSubKey} → {AutoStartEntry.ValueName} = {AutoStartEntry.WindowsCommand(executable)}";
            }

            return $"文件 {AutoStartEntry.EntryFilePath}";
        }

        [SupportedOSPlatform("windows")]
        private static AutoStartStatus ReadWindows(string executable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartEntry.WindowsRunSubKey);
            var target = AutoStartEntry.ParseWindowsCommand(key?.GetValue(AutoStartEntry.ValueName) as string);
            if (target is null)
            {
                return new AutoStartStatus(AutoStartState.NotRegistered, null);
            }

            return new AutoStartStatus(
                AutoStartEntry.IsCurrentTarget(target, executable) ? AutoStartState.Registered : AutoStartState.RegisteredOtherPath,
                target);
        }

        private static AutoStartStatus ReadEntryFile(string executable)
        {
            var file = AutoStartEntry.EntryFilePath;
            if (file is null || !File.Exists(file))
            {
                return new AutoStartStatus(AutoStartState.NotRegistered, null);
            }

            // 不解析条目正文里的路径:注册时写进去的就是当前程序路径,一次包含判断足够判断“是不是指向本程序”;
            // 具体指向哪由 DescribeRegistration 给出的文件位置去查(各平台正文格式不同,解析不划算)。
            var matches = File.ReadAllText(file).Contains(executable, StringComparison.OrdinalIgnoreCase);
            return new AutoStartStatus(
                matches ? AutoStartState.Registered : AutoStartState.RegisteredOtherPath,
                null);
        }

        [SupportedOSPlatform("windows")]
        private static string? WriteWindows(bool enabled, string executable)
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(AutoStartEntry.WindowsRunSubKey);
                if (key is null)
                {
                    return $@"打不开注册表项 HKCU\{AutoStartEntry.WindowsRunSubKey}";
                }

                key.SetValue(AutoStartEntry.ValueName, AutoStartEntry.WindowsCommand(executable), RegistryValueKind.String);
                return null;
            }

            // 关闭用 OpenSubKey(true):键不存在就该什么都不做,不该顺手建一个空键出来
            using var runKey = Registry.CurrentUser.OpenSubKey(AutoStartEntry.WindowsRunSubKey, writable: true);
            runKey?.DeleteValue(AutoStartEntry.ValueName, throwOnMissingValue: false);
            return null;
        }

        private static string? WriteEntryFile(bool enabled, string executable)
        {
            var file = AutoStartEntry.EntryFilePath
                ?? throw new InvalidOperationException("无法确定自启动项文件位置");

            if (!enabled)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return null;
            }

            // ⚠ macOS 不额外调用 launchctl:LaunchAgent 由 launchd 在**登录时**加载,
            // 而“关闭”只要文件不在了,下次登录就不会再被加载;当前这次会话里应用本来就在运行,
            // 现在 load 一次毫无意义(还要多一条没法在本机验证的 shell 调用)。
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, AutoStartEntry.EntryFileContent(executable));
            return null;
        }

        /// <summary>
        /// 把异常翻译成用户能理解的一句话。权限/策略是最可能的失败原因(注册表项可被组策略或安全软件锁住),
        /// 单独给一句说法,比抛一个英文异常类型有用得多。
        /// </summary>
        private static string DescribeFailure(Exception ex) => ex switch
        {
            UnauthorizedAccessException or SecurityException => "权限不足,或系统策略/安全软件阻止了写入",
            PlatformNotSupportedException => "当前平台不支持注册开机自启动",
            _ => $"{ex.GetType().Name}: {ex.Message}",
        };
    }
}
