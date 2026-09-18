using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 管理 npm 全局包 @deepseek-ai/dsh 的检测、安装与运行(单例,跨页面保持状态)。
    /// 所有事件可能在后台线程触发,UI 层需自行调度到 UI 线程。
    /// </summary>
    public sealed partial class DshService
    {
        public static DshService Instance { get; } = new();

        private const string PackageName = "@deepseek-ai/dsh";
        private const string CommandName = "dsh";

        /// <summary>固定的子命令部分;监听地址与端口由 <see cref="BuildRunArgs"/> 按设置追加。</summary>
        private const string BaseRunArgs = "web --no-open";

        private Process? _process;

        /// <summary>
        /// 日志缓冲区。日志同时被多个线程写:stdout/stderr 的异步回调(线程池)、
        /// RunStreamingAsync 的安装输出回调、UI 线程的 AppendSystemLog;读侧还有 LogText、
        /// TryRecordStartFailure 与 ClearLog —— 线程安全与长度裁剪都由 <see cref="LogBuffer"/> 负责。
        /// </summary>
        /// <remarks>
        /// 上限不是"省内存"的锦上添花,而是**防界面假死**的硬要求:dsh 启动失败时 Node 会把
        /// 整个 AggregateError 树完整打印(实测一次 43 万字符),而面板是"整段文本 + 每行追加全量重排"
        /// 的实现,不限长就会把 UI 线程钉死在 Avalonia 文本排版里(窗口"未响应")。
        /// 面板本来就是实时尾部视图,更早的内容另有 app.log 文件可查。
        /// </remarks>
        private readonly LogBuffer _log = new(LogBuffer.DefaultMaxChars);

        /// <summary>本次运行输出的开头(<see cref="CaptureRunHead"/>),供启动失败时写进 app.log。</summary>
        private readonly StringBuilder _runHead = new();

        /// <summary><see cref="_runHead"/> 的锁(日志会被多个线程追加)。</summary>
        private readonly Lock _runHeadLock = new();

        /// <summary>本次运行产生的输出总字符数(含已被面板上限裁掉的部分)。</summary>
        private int _runOutputLength;

        private DateTime _startTimeUtc = DateTime.MinValue;
        private int _lastStartLogMark;
        private volatile bool _stopRequestedByUser;
        private bool _startFailureHandled;

        /// <summary>是否正处在“补齐依赖后重试”的序列中(用来抑制中间那次失败弹框)。</summary>
        private volatile bool _startRetryInProgress;

        /// <summary>最近一次启动实际使用的参数(错误信息中展示,便于用户复现)。</summary>
        private string _lastRunArgs = BaseRunArgs;

        public event Action<string>? LogAppended;
        public event Action? StateChanged;
        public event Action? LogsCleared;

        /// <summary>启动后在短时间内意外退出(非用户手动停止)时触发,用于弹出错误提示。</summary>
        public event Action? StartFailed;

        /// <summary>从 stdio 中检测到 Web 服务地址时触发。</summary>
        public event Action<string>? WebUrlDetected;

        /// <summary>从 stdio 中检测到的 Web 服务地址(如 http://localhost:xxxx);未检测到为 null。</summary>
        public string? WebUrl { get; private set; }

        private static readonly Regex WebUrlRegex = GetWebUrlRegex();

        public bool IsRunning => IsAlive(this._process);

        public string? InstalledVersion { get; private set; }
        public bool IsInstalling { get; private set; }

        /// <summary>面板上要显示的日志正文(超长时只保留尾部,并在开头带一行"已省略"提示)。</summary>
        public string LogText => this._log.ToString();

        /// <summary>npm 上的最新版本号;尚未检查或检查失败时为 null。</summary>
        public string? LatestVersion { get; private set; }

        /// <summary>是否存在可用的新版本(已安装且 npm 上版本更新)。</summary>
        public bool IsUpdateAvailable =>
            this.InstalledVersion is not null
            && this.LatestVersion is not null
            && CompareVersions(this.LatestVersion, this.InstalledVersion) > 0;

        /// <summary>运行中进程的 PID;未运行时为 null。</summary>
        public int? ProcessId
        {
            get
            {
                var process = this._process;
                return IsAlive(process) ? process!.Id : null;
            }
        }

        /// <summary>
        /// 进程是否仍活着。
        /// ⚠ 不能直接写 <c>process.HasExited</c>:对**从未启动**的 Process(例如
        /// <c>process.Start()</c> 抛异常后残留在 <c>_process</c> 上的那个)访问 HasExited 会抛
        /// <see cref="InvalidOperationException"/>("No process is associated with this object"),
        /// 而本判断会被 UI 状态刷新、停止/重启、关机兜底等一大票路径读取,抛出去就是未处理异常。
        /// </summary>
        private static bool IsAlive(Process? process)
        {
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // 从未启动成功(ObjectDisposedException 也派生自 InvalidOperationException,一并覆盖)
                return false;
            }
        }

        /// <summary>
        /// 本次运行的启动时刻(本地时间);未运行时为 null。
        /// 用“启动时刻”而非“已运行时长”:后者会随时间变陈旧,而界面只在状态变化时刷新。
        /// </summary>
        public DateTime? StartedAtLocal =>
            this.IsRunning && this._startTimeUtc != DateTime.MinValue
                ? this._startTimeUtc.ToLocalTime()
                : null;

        /// <summary>最近一次启动失败的原因(命令、退出代码与输出)。</summary>
        public string LastStartError { get; private set; } = string.Empty;

        /// <summary>判定为“启动失败”的等待窗口:进程在该时间内退出则视为失败。</summary>
        private static readonly TimeSpan StartProbeWindow = TimeSpan.FromSeconds(3);

        /// <summary>启动成功后的观察期:观察期内意外退出仍视为启动失败并通知 UI。</summary>
        private static readonly TimeSpan LateExitFailureWindow = TimeSpan.FromSeconds(15);

        private DshService()
        {
        }

        /// <summary>
        /// 依据设置拼接 dsh 启动参数。
        /// 端口为 0(未设置)时不追加 --port,沿用 dsh 自身默认端口 3080。
        /// 监听地址不提供配置:dsh 的 CLI 明确拒绝 --host 0.0.0.0
        /// ("intentionally not supported yet for safety"),而 127.0.0.1 本就是默认值,
        /// 因此传 --host 没有任何意义。
        /// </summary>
        private static string BuildRunArgs()
        {
            var settings = SettingsService.Instance.Settings;
            var args = new StringBuilder(BaseRunArgs);

            var port = settings.ListenPort is >= 1 and <= 65535 ? settings.ListenPort : 0;
            if (port > 0)
            {
                args.Append(" --port ").Append(port.ToString(CultureInfo.InvariantCulture));
            }

            return args.ToString();
        }

        /// <summary>
        /// 追加日志。detectWebUrl 仅在 dsh 进程输出流调用时为 true(只有进程输出可能含 Web 地址),
        /// 其余日志(安装输出、系统信息、启停标记)一律跳过检测。
        /// </summary>
        private void AppendLog(string text, bool detectWebUrl = false)
        {
            // 超长时由 LogBuffer 从头部按整行裁剪(见 LogBuffer.DefaultMaxChars 的说明)
            this._log.Append(text);
            this.CaptureRunHead(text);

            if (detectWebUrl)
            {
                this.TryDetectWebUrl(text);
            }

            // 事件在锁外触发:处理器会 Post 到 UI 线程,放在锁里没有好处,只会扩大临界区
            LogAppended?.Invoke(text);
        }

        /// <summary>失败留证里"本次运行输出的开头"的字符数上限。</summary>
        private const int RunHeadChars = 2_000;

        /// <summary>
        /// 记住本次运行输出的**开头**。
        /// 为什么要单独存:面板正文(<see cref="LogBuffer"/>)有长度上限、且只保留尾部,
        /// 而排查启动失败时最有价值的恰好是开头(第一处报错);拉日志时若直接从缓冲区取开头,
        /// 拿到的其实是被裁剪后的那段(开头已经是"已省略 N 字符"的提示行)。
        /// </summary>
        private void CaptureRunHead(string text)
        {
            lock (this._runHeadLock)
            {
                this._runOutputLength += text.Length;

                var room = RunHeadChars - this._runHead.Length;
                if (room > 0)
                {
                    this._runHead.Append(text.Length <= room ? text : text[..room]);
                }
            }
        }

        /// <summary>记录应用级诊断信息(显示在首页日志面板,同时写入文件日志)。</summary>
        public void AppendSystemLog(string message)
        {
            this.AppendLog($"[应用] {message}\r\n");
            AppLogService.Write(message);
        }

        /// <summary>
        /// 清空日志文本并通知 UI。
        /// 注意:这里**不**置空 <see cref="WebUrl"/> —— 服务仍在运行,地址依然有效,
        /// 清日志不该让"打开/复制地址"的入口消失。该字段只在 Stop/Restart/进程退出时清空。
        /// </summary>
        public void ClearLog()
        {
            this._log.Clear();

            lock (this._runHeadLock)
            {
                this._runHead.Clear();
            }

            // ⚠ 必须一起复位:_runOutputLength 是“本次运行”的累计输出量,不归零时每次重试都会累加,
            // app.log 里会出现 431593 / 863292 / 1294991 这种“成倍增长”的假象(实测踩到)。
            this._runOutputLength = 0;

            LogsCleared?.Invoke();
        }

        /// <summary>从当前运行进程的输出中检测 Web 服务地址(一次成功后由守卫短路,不再执行正则)。</summary>
        private void TryDetectWebUrl(string text)
        {
            if (this.WebUrl is not null || !this.IsRunning)
            {
                return;
            }

            var match = WebUrlRegex.Match(text);
            if (match.Success)
            {
                // 丢掉末尾可能被一起抓到的标点(如句号、括号)
                var raw = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', ']', '>', '\'', '\"');

                // 归一化监听地址:0.0.0.0/[::1] 在浏览器里不可访问,换成 localhost
                var url = raw
                    .Replace("://0.0.0.0:", "://localhost:", StringComparison.OrdinalIgnoreCase)
                    .Replace("://[::1]:", "://localhost:", StringComparison.OrdinalIgnoreCase);
                this.WebUrl = url;
                WebUrlDetected?.Invoke(url);
            }
        }

        /// <summary>查询 npm 全局安装的版本号;未安装时返回 null。</summary>
        public async Task<string?> GetInstalledVersionAsync()
        {
            try
            {
                var (stdout, _) = await RunCaptureAsync($"npm ls -g {PackageName} --depth=0");
                var match = Regex.Match(stdout, Regex.Escape(PackageName) + @"@([^\s]+)");
                this.InstalledVersion = match.Success ? match.Groups[1].Value : null;
            }
            catch (Exception ex)
            {
                this.InstalledVersion = null;
                this.AppendLog($"[检查安装状态失败] {ex.Message}\r\n");
            }
            return this.InstalledVersion;
        }

        /// <summary>通过 npm 全局安装该包,输出写入日志。</summary>
        public async Task InstallAsync()
        {
            if (this.IsInstalling)
            {
                return;
            }

            this.IsInstalling = true;
            StateChanged?.Invoke();
            try
            {
                this.AppendLog($"> npm install -g {PackageName}\r\n");
                await this.RunStreamingAsync($"npm install -g {PackageName}");
            }
            finally
            {
                this.IsInstalling = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 查询 npm 上的最新版本号,用于判断是否需要显示“更新”按钮。
        /// 未安装时不查询(没有可更新的对象);查询失败(离线、npm 报错)时把 LatestVersion 置空,
        /// 即“不确定就当作无更新”,避免误报。
        /// </summary>
        public async Task CheckForUpdateAsync()
        {
            if (this.InstalledVersion is null)
            {
                this.LatestVersion = null;
                return;
            }

            try
            {
                var (stdout, _) = await RunCaptureAsync($"npm view {PackageName} version");
                // 输出形如 "1.2.3";多行时取最后一个非空行(npm 可能先打印告警)
                var latest = stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .LastOrDefault(line => line.Length > 0);
                this.LatestVersion = string.IsNullOrWhiteSpace(latest) ? null : latest;
            }
            catch (Exception ex)
            {
                this.LatestVersion = null;
                this.AppendLog($"[检查更新失败] {ex.Message}\r\n");
            }

            this.AppendSystemLog($"[版本检查] 已安装 v{this.InstalledVersion},npm 最新 v{this.LatestVersion ?? "(未知)"}"
                + $"{(this.IsUpdateAvailable ? ",有新版本可用" : ",已是最新")}");
            StateChanged?.Invoke();
        }

        /// <summary>
        /// 更新到最新版:重新执行 npm 全局安装(npm install -g 会升级到 latest),
        /// 完成后刷新已安装版本与最新版本,使“更新”按钮自动消失。
        /// </summary>
        public async Task UpdateAsync()
        {
            if (this.IsInstalling || this.InstalledVersion is null)
            {
                return;
            }

            this.AppendLog($"[更新] 当前 v{this.InstalledVersion} → 最新 v{this.LatestVersion ?? "latest"}\r\n");
            await this.InstallAsync();
            await this.GetInstalledVersionAsync();
            await this.CheckForUpdateAsync();
        }

        /// <summary>
        /// 比较语义化版本号:返回 &lt;0 表示 a 早于 b,0 表示相同,&gt;0 表示 a 晚于 b。
        /// 只比较数字部分(缺失的段按 0 处理);数字相同时,带预发布标记的视为更早
        /// (如 1.0.0-beta &lt; 1.0.0)。
        /// </summary>
        private static int CompareVersions(string a, string b)
        {
            static (int[] Numbers, string Prerelease) Split(string value)
            {
                var text = value.Trim().TrimStart('v', 'V');
                var dash = text.IndexOf('-');
                var core = dash >= 0 ? text[..dash] : text;
                var prerelease = dash >= 0 ? text[(dash + 1)..] : string.Empty;
                var numbers = core
                    .Split('.')
                    .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                    .ToArray();
                return (numbers, prerelease);
            }

            var (aNumbers, aPre) = Split(a);
            var (bNumbers, bPre) = Split(b);

            var length = Math.Max(aNumbers.Length, bNumbers.Length);
            for (var i = 0; i < length; i++)
            {
                var av = i < aNumbers.Length ? aNumbers[i] : 0;
                var bv = i < bNumbers.Length ? bNumbers[i] : 0;
                if (av != bv)
                {
                    return av.CompareTo(bv);
                }
            }

            if (aPre.Length == 0 && bPre.Length == 0)
            {
                return 0;
            }
            if (aPre.Length == 0)
            {
                return 1;
            }
            if (bPre.Length == 0)
            {
                return -1;
            }
            return string.CompareOrdinal(aPre, bPre);
        }

        /// <summary>启动 dsh 进程(stdio 重定向到日志);对“profile 依赖解析失败”再给一次机会。</summary>
        /// <remarks>
        /// <para>
        /// 真正的根因(2026-09-18 端到端实验闭环):被别的进程当子进程拉起的实例(典型来源是安装器
        /// “安装完成后启动”)处于**上游进程链**里,Windows 在加载器层随链继承的 AppCompat shim 本体
        /// 让 launcher 派生的 <c>cmd</c>/<c>node</c>(dsh) 对 profile 依赖的 junction 大面积不可达 ⇒ 整片
        /// <c>Cannot find package '@deepseek-ai/*'</c>。清子进程环境变量拦不住(载体是进程内的 shim,
        /// 环境变量只是影子),正解是在启动时**逃逸出上游进程链**重开自己
        /// (见 App.axaml.cs 的 IsInsideInheritedCompatChain / TryEscapeInheritedChain)。
        /// </para>
        /// <para>
        /// 这里的重试只是兜底:仅对“profile 依赖解析失败”这一种特征再试一次;端口占用、配置错误等一次就报。
        /// 失败当刻会拍依赖层快照留证(见 <see cref="CaptureResolutionDiagnosticsAsync"/>)。
        /// </para>
        /// </remarks>
        public async Task<bool> StartAsync()
        {
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    var ok = await this.StartOnceAsync();
                    if (ok || this._stopRequestedByUser || attempt >= StartAttempts
                        || !IsProfileResolutionFailure(this.LastStartError))
                    {
                        return ok;
                    }

                    // 中间尝试的失败不弹框(避免用一次注定要重试的失败打扰用户)
                    this._startRetryInProgress = true;
                    this.AppendSystemLog(
                        $"[启动] profile 依赖解析失败(第 {attempt} 次尝试),{TransientRetryDelay.TotalSeconds:0} 秒后重试…");

                    // 必须在失败当刻拍快照:重跑会改变各层依赖状态,事后再拍就不是失败现场了
                    await this.CaptureResolutionDiagnosticsAsync(this.LastStartError);
                    await Task.Delay(TransientRetryDelay);
                }
            }
            finally
            {
                this._startRetryInProgress = false;
            }
        }

        /// <summary>
        /// 单次启动:where 定位 npm shim → 完整路径启动 → 探测窗口内退出即失败;
        /// 返回 false 表示启动失败(已填充 <see cref="LastStartError"/>)。
        /// </summary>
        private async Task<bool> StartOnceAsync()
        {
            if (this.IsRunning || this.IsInstalling)
            {
                return true;
            }

            this._stopRequestedByUser = false;
            this._startFailureHandled = false;
            this.ClearLog();
            this._lastStartLogMark = 0;
            this._lastRunArgs = BuildRunArgs();
            this.AppendLog($"> {CommandName} {this._lastRunArgs}\r\n");
            // 1) 先定位 npm 全局 bin 下的 shim,给出明确错误,避免“进程活着但命令没跑起来”的模糊状态
            string shimPath;
            try
            {
                var (found, probeOutput) = await FindDshShimAsync();
                shimPath = found;
                if (shimPath.Length == 0)
                {
                    this.LastStartError = $"命令: {CommandName} {this._lastRunArgs}\r\n\r\n未找到“{CommandName}”命令。"
                        + "npm 全局 bin 目录可能不在 PATH 中,或包未正确安装。"
                        + $"\r\n\r\n{PlatformProcess.LocateCommandLine(CommandName)} 输出:\r\n{probeOutput}";
                    this.AppendLog("[启动失败] 未找到 dsh 命令\r\n");
                    return false;
                }
            }
            catch (Exception ex)
            {
                this.LastStartError = $"命令: {CommandName} {this._lastRunArgs}\r\n\r\n定位命令失败:\r\n{ex}";
                this.AppendLog($"[启动失败] 定位命令失败: {ex.Message}\r\n");
                return false;
            }

            // 2) 用完整路径通过系统 shell 启动:不依赖调用方进程的 PATH。
            //    Windows: cmd.exe /c call "<shim>" args(.cmd shim 必须经 cmd 解释);
            //    类 Unix: /bin/zsh -lc '<shim> args'(经登录 shell 装配 PATH,才能找到 node)。
            Process process;
            var started = false;
            try
            {
                // 这里**不**注入“环境设置”的代理:这是本机服务,WebView/浏览器要访问 127.0.0.1,
                // 注入了代理反而会绕一圈甚至失败(npm 源同理,与运行服务无关)。
                var psi = PlatformProcess.CreateShellStartInfo(
                    PlatformProcess.ShellCommandForExecutable(shimPath, this._lastRunArgs),
                    redirectOutput: true,
                    redirectInput: true,
                    rawByteOutput: true);

                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        this.AppendLog(PlatformProcess.DecodeChildOutputLine(e.Data) + "\r\n", detectWebUrl: true);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        this.AppendLog(PlatformProcess.DecodeChildOutputLine(e.Data) + "\r\n", detectWebUrl: true);
                    }
                };
                process.Exited += (_, _) => this.OnProcessExited(process);

                // 必须在 Start 之前赋值 this._process:IsRunning 依赖该字段,
                // 而 stdout 事件可能在 Start 返回后瞬间到达,若此时 _process 仍为 null,
                // TryDetectWebUrl 会因 !IsRunning 跳过 Web 地址检测(Web 地址只打印一次,错过即丢失)
                this._process = process;
                process.Start();
                started = true;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 绑定到"关闭即杀"作业对象:应用意外退出(崩溃/强杀)时由内核自动停止整个进程树
                this.AttachProcessToJob(process);
            }
            catch (Exception ex)
            {
                this.LastStartError = $"命令: {CommandName} {this._lastRunArgs}\r\n\r\n无法启动进程:\r\n{ex}";
                this.AppendLog($"[启动失败] {ex.Message}\r\n");

                // 进程压根没起来时(process.Start() 抛异常),_process 里留着的是一个"从未启动"的
                // Process 对象 —— 之后任何 IsRunning/ProcessId/Stop 都会因 HasExited 抛
                // InvalidOperationException。必须把它摘掉,失败的实例也一并释放。
                // 反之,进程已经启动成功(后面某一行才抛)时必须保留跟踪,否则会漏掉一个在跑的 dsh。
                if (!started)
                {
                    var failed = this._process;
                    this._process = null;
                    try
                    {
                        failed?.Dispose();
                    }
                    catch
                    {
                        // 释放失败不影响后续流程
                    }
                }

                return false;
            }

            this._startTimeUtc = DateTime.UtcNow;
            StateChanged?.Invoke();

            // 3) 探测窗口(后台线程阻塞等待,避免卡 UI):窗口内退出 → 失败
            var exitedInWindow = await Task.Run(() => process.WaitForExit((int)StartProbeWindow.TotalMilliseconds));
            if (!exitedInWindow)
            {
                return true; // 进程存活,视为启动成功
            }

            // 4) 进程已退出:按官方文档再调一次无参 WaitForExit,等待异步输出读取全部完成,避免日志截断
            await Task.Run(process.WaitForExit);
            this.TryRecordStartFailure(process, this._lastStartLogMark);
            return false;
        }

        /// <summary>进程退出回调:观察期内的意外退出视为启动失败,通知 UI 弹窗。</summary>
        private void OnProcessExited(Process process)
        {
            var failedEarly = !this._stopRequestedByUser
                && ReferenceEquals(this._process, process)
                && this._startTimeUtc != DateTime.MinValue
                && DateTime.UtcNow - this._startTimeUtc < LateExitFailureWindow;

            this.WebUrl = null;
            StateChanged?.Invoke();

            if (failedEarly)
            {
                // 稍候片刻并等待冲刷,确保错误输出已写入日志后再生成错误信息
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(300);
                        process.WaitForExit();
                    }
                    catch
                    {
                        // 进程对象可能已被释放,忽略
                    }

                    // 重试序列进行中时不弹框:那只是中间一次失败,StartAsync 还会补齐依赖再试
                    var recorded = this.TryRecordStartFailure(process, this._lastStartLogMark);
                    if (recorded && !this._startRetryInProgress)
                    {
                        // 探测窗口外的解析失败同样留证(3~15 秒才退出的失败不经过 StartAsync 重试循环,
                        // 不在这里拍快照就永远没有失败当刻的四层状态了)
                        if (IsProfileResolutionFailure(this.LastStartError))
                        {
                            _ = this.CaptureResolutionDiagnosticsAsync(this.LastStartError);
                        }

                        StartFailed?.Invoke();
                    }
                });
            }
        }

        /// <summary>
        /// 记录启动失败(每次启动只记录一次:探测窗口路径与退出回调路径可能并发触发)。
        /// 返回 true 表示本次调用是首次记录。
        /// </summary>
        private bool TryRecordStartFailure(Process process, int logMark)
        {
            if (this._startFailureHandled)
            {
                return false;
            }

            this._startFailureHandled = true;

            // LogBuffer.ReadFrom 内部会对下标做夹取(调用方可能并发点了「清空」,
            // 或者这段输出已因超长被从头部裁掉),并把"已省略"提示行补在最前面 ——
            // 因此这里拿到的长度是有上限的,不会把几十万字符挂进 LastStartError。
            var output = this._log.ReadFrom(logMark).TrimEnd();

            var exitCode = 0;
            try
            {
                exitCode = process.ExitCode;
            }
            catch
            {
                // 退出码不可用时保持 0
            }

            var hints = AnalyzeFailureOutput(output);
            this.LastStartError = $"命令: {CommandName} {this._lastRunArgs}\r\n退出代码: {exitCode}\r\n\r\n可能的原因:\r\n{ProfileResolutionHintFor(output)}{hints}\r\n\r\n完整输出:\r\n{output}";
            this.AppendLog($"[启动失败,进程已退出,代码 {exitCode}]\r\n");

            // 面板正文有长度上限(只保留尾部),而排查时最有价值的往往是**开头**(第一处错误):
            // 这里把本次运行输出的开头写进 app.log —— 文件日志不受面板上限影响,事后可查。
            AppLogService.Write($"[启动失败] {CommandName} {this._lastRunArgs} / 退出代码 {exitCode} / "
                + $"缓冲区 {output.Length} 字符,本次运行输出共 {this.RunOutputLength} 字符,开头 {RunHeadChars} 字符如下:\r\n{this.RunHeadText()}");
            return true;
        }

        /// <summary>取本次运行输出的开头(带省略标记),供 app.log 留证。</summary>
        private string RunHeadText()
        {
            lock (this._runHeadLock)
            {
                var head = this._runHead.ToString();
                return head.Length == 0
                    ? "(本次运行没有产生任何输出)"
                    : head + (head.Length >= RunHeadChars ? "\r\n…(只记录开头)" : string.Empty);
            }
        }

        /// <summary>本次运行产生的输出总字符数(含已被面板上限裁掉的部分)。</summary>
        private int RunOutputLength
        {
            get
            {
                lock (this._runHeadLock)
                {
                    return this._runOutputLength;
                }
            }
        }

        /// <summary>
        /// 重启:停止当前进程(用户主动操作,不弹失败提示),等其完全退出后重新启动。
        /// 返回 false 表示重启后的启动失败(已填充 LastStartError)。
        /// </summary>
        public async Task<bool> RestartAsync()
        {
            var process = this._process;
            if (process is null)
            {
                return await this.StartAsync();
            }

            // 标记为用户主动停止,避免退出回调误判为启动失败
            this._stopRequestedByUser = true;
            if (IsAlive(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    this.WebUrl = null;
                    this.AppendLog("\r\n[正在重启,进程已停止]\r\n");
                }
                catch (Exception ex)
                {
                    this.AppendLog($"[停止旧进程失败,仍尝试启动] {ex.Message}\r\n");
                }
            }

            // 等待旧进程(含子进程树)完全退出,释放端口后再启动
            try
            {
                await Task.Run(process.WaitForExit);
            }
            catch
            {
                // 进程对象可能已被释放,忽略
            }

            this._process = null;
            this.StateChanged?.Invoke();

            return await this.StartAsync();
        }

        /// <summary>启动的总尝试次数(1 次正常启动 + 1 次兜底重试)。</summary>
        private const int StartAttempts = 2;

        /// <summary>兜底重试前的等待(给文件系统/上一个进程一点收尾时间;不是等锁)。</summary>
        private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(3);

        /// <summary>dsh 的 profile 名(与 <see cref="BaseRunArgs"/> 的子命令一致:profiles\web)。</summary>
        private const string ProfileName = "web";

        /// <summary>
        /// 停止 dsh 进程(连同子进程树),并关闭 WebView 窗口。
        /// 注意:重启走 <see cref="RestartAsync"/>,不经过这里,因此重启时 WebView 窗口会保留下来复用。
        /// </summary>
        public void Stop()
        {
            this._stopRequestedByUser = true;

            var process = this._process;
            if (process is not null && IsAlive(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    this.WebUrl = null;
                    this.AppendLog("\r\n[进程已停止]\r\n");
                }
                catch (Exception ex)
                {
                    this.AppendLog($"[停止进程失败] {ex.Message}\r\n");
                }
                finally
                {
                    // 关闭作业句柄兜底:即使 Kill 失败,内核也会终止作业内残留的全部进程
                    this.CloseJobHandle();
                }
            }

            // 服务已停,WebView 里只剩一个连不上的死页面:真正关闭窗口并释放 WebView2 进程(约 470MB)。
            // 这里不采用“隐藏”——服务都停了,保留窗口只会白占内存。
            WebOpener.CloseSession();
        }

        private static async Task<(string Stdout, string Stderr)> RunCaptureAsync(string command)
        {
            using var process = StartHidden(command);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (await stdout, await stderr);
        }

        /// <summary>
        /// 用定位到的 dsh shim 执行一条 dsh 子命令并捕获输出(供插件管理等场景复用;
        /// 复用同一套 shim 定位逻辑,避免两处各自找命令而行为不一致)。
        /// 返回码 -1 表示未能定位 dsh 命令(说明见 Stderr)。
        /// </summary>
        public async Task<(int ExitCode, string Stdout, string Stderr)> RunDshCaptureAsync(string arguments)
        {
            var (shimPath, locateError) = await TryLocateShimAsync();
            if (shimPath.Length == 0)
            {
                return (-1, string.Empty, locateError);
            }

            // dsh(node)输出 UTF-8,但 cmd 自身的错误消息是 OEM 代码页 —— 按原样字节收下再逐行判定
            using var process = StartHidden(PlatformProcess.ShellCommandForExecutable(shimPath, arguments), rawByteOutput: true);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode,
                PlatformProcess.DecodeChildOutputText(await stdout),
                PlatformProcess.DecodeChildOutputText(await stderr));
        }

        /// <summary>
        /// 用定位到的 dsh shim 执行一条 dsh 子命令,输出按行流式回传(用于需要实时反馈的操作,如安装/卸载插件)。
        /// 返回码 -1 表示未能定位 dsh 命令。
        /// </summary>
        public async Task<int> RunDshStreamingAsync(string arguments, Action<string> onOutput)
        {
            var (shimPath, locateError) = await TryLocateShimAsync();
            if (shimPath.Length == 0)
            {
                onOutput(locateError);
                return -1;
            }

            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // dsh/pnpm(node)写 UTF-8,cmd 自身消息是 OEM 代码页 —— 原样收下再逐行判定
            var process = StartHidden(PlatformProcess.ShellCommandForExecutable(shimPath, arguments), rawByteOutput: true);
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onOutput(PlatformProcess.DecodeChildOutputLine(e.Data));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onOutput(PlatformProcess.DecodeChildOutputLine(e.Data));
                }
            };
            process.Exited += (_, _) => exited.TrySetResult();

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await exited.Task;

            // 无参 WaitForExit 会等待异步输出读取完毕,避免 pnpm 末尾输出被截断
            await Task.Run(process.WaitForExit);
            var exitCode = process.ExitCode;
            process.Dispose();
            return exitCode;
        }

        /// <summary>定位 dsh shim;失败时返回空串与可直接展示的错误说明。</summary>
        private static async Task<(string ShimPath, string Error)> TryLocateShimAsync()
        {
            try
            {
                var (found, probeOutput) = await FindDshShimAsync();
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
        private static async Task<(string ShimPath, string ProbeOutput)> FindDshShimAsync()
        {
            var probeCommand = PlatformProcess.LocateCommandLine(CommandName);
            var (probeOut, probeErr) = await RunCaptureAsync(probeCommand);

            var candidates = probeOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);

            var shimPath = PlatformProcess.IsWindows
                ? candidates.FirstOrDefault(line => line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                                    || line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? string.Empty
                : candidates.FirstOrDefault() ?? string.Empty;

            return (shimPath, (probeOut + probeErr).TrimEnd());
        }

        /// <summary>
        /// 查询用于排查启动问题的环境信息:Node / npm 版本、dsh 命令路径。
        /// 任一项查不到时返回 null(界面显示为未知),不抛异常。
        /// </summary>
        public async Task<(string? Node, string? Npm, string? DshPath)> GetEnvironmentInfoAsync()
        {
            var node = await TryCaptureFirstLineAsync("node --version");
            var npm = await TryCaptureFirstLineAsync("npm --version");

            string? dshPath = null;
            try
            {
                var (shim, _) = await FindDshShimAsync();
                dshPath = shim.Length > 0 ? shim : null;
            }
            catch (Exception)
            {
                // 定位失败时保持 null
            }

            return (node, npm, dshPath);
        }

        /// <summary>执行命令并取首个非空输出行;失败返回 null。</summary>
        private static async Task<string?> TryCaptureFirstLineAsync(string command)
        {
            try
            {
                var (stdout, _) = await RunCaptureAsync(command);
                return stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Length > 0);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>运行命令并把 stdout/stderr 流式追加到日志,进程退出后返回。</summary>
        private async Task RunStreamingAsync(string command)
        {
            // npm install 流程(npm 输出不含 Web 地址);npm 是 Node 程序、写 UTF-8,
            // 而 cmd 自身消息是 OEM 代码页 —— 原样收下再逐行判定
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = StartHidden(command, rawByteOutput: true);
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    this.AppendLog(PlatformProcess.DecodeChildOutputLine(e.Data) + "\r\n");
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    this.AppendLog(PlatformProcess.DecodeChildOutputLine(e.Data) + "\r\n");
                }
            };
            process.Exited += (_, _) =>
            {
                this.AppendLog($"[npm 退出,代码 {process.ExitCode}]\r\n");
                exited.SetResult();
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await exited.Task;
        }

        /// <summary>
        /// 静默执行一条命令行并捕获 stdout/stderr。
        /// Windows 走 cmd.exe /c,类 Unix 走登录 shell -lc(见 <see cref="PlatformProcess"/>)。
        /// <para>
        /// 统一注入“环境设置”(npm 源 / 代理,见 <see cref="ChildEnvironment"/>)——
        /// 这里是**所有非 dsh web 子进程**的公共出口(npm 安装/查询、命令探测、dsh 子命令),
        /// 所以只需在这一处注入,不必在每个调用点重复。
        /// <c>dsh web</c> 服务进程**不**走这里(它要连本机 127.0.0.1,不该被代理接管)。
        /// </para>
        /// </summary>
        /// <param name="rawByteOutput">
        /// 是否按“原样字节”收下输出,交给调用方用 <see cref="PlatformProcess.DecodeChildOutputLine"/> 判定编码。
        /// 凡输出会被展示或解析的路径都要传 true(Node 写 UTF-8、cmd 自身消息是 OEM 代码页,两者会混在一起)。
        /// </param>
        private static Process StartHidden(string command, bool rawByteOutput = false)
        {
            var psi = PlatformProcess.CreateShellStartInfo(
                command, redirectOutput: true, rawByteOutput: rawByteOutput);
            ChildEnvironment.Apply(psi);
            return Process.Start(psi)!;
        }

        [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):\d+(?:/[^\s""'<>]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
        private static partial Regex GetWebUrlRegex();
    }
}
