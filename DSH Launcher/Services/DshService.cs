using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        private readonly StringBuilder _log = new();
        private DateTime _startTimeUtc = DateTime.MinValue;
        private int _lastStartLogMark;
        private volatile bool _stopRequestedByUser;
        private bool _startFailureHandled;

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

        public bool IsRunning => this._process is { HasExited: false };
        public string? InstalledVersion { get; private set; }
        public bool IsInstalling { get; private set; }
        public string LogText => this._log.ToString();

        /// <summary>npm 上的最新版本号;尚未检查或检查失败时为 null。</summary>
        public string? LatestVersion { get; private set; }

        /// <summary>是否存在可用的新版本(已安装且 npm 上版本更新)。</summary>
        public bool IsUpdateAvailable =>
            this.InstalledVersion is not null
            && this.LatestVersion is not null
            && CompareVersions(this.LatestVersion, this.InstalledVersion) > 0;

        /// <summary>运行中进程的 PID;未运行时为 null。</summary>
        public int? ProcessId => this._process is { HasExited: false } p ? p.Id : null;

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
            this._log.Append(text);
            if (detectWebUrl)
            {
                this.TryDetectWebUrl(text);
            }
            LogAppended?.Invoke(text);
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

        /// <summary>
        /// 启动 dsh 进程(stdio 重定向到日志)。
        /// 流程:where 定位 npm shim → 完整路径启动 → 探测窗口内退出即失败;
        /// 返回 false 表示启动失败(已填充 LastStartError)。
        /// </summary>
        public async Task<bool> StartAsync()
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
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 绑定到"关闭即杀"作业对象:应用意外退出(崩溃/强杀)时由内核自动停止整个进程树
                this.AttachProcessToJob(process);
            }
            catch (Exception ex)
            {
                this.LastStartError = $"命令: {CommandName} {this._lastRunArgs}\r\n\r\n无法启动进程:\r\n{ex}";
                this.AppendLog($"[启动失败] {ex.Message}\r\n");
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

                    if (this.TryRecordStartFailure(process, this._lastStartLogMark))
                    {
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
            var output = this._log.ToString(logMark, this._log.Length - logMark).TrimEnd();
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
            this.LastStartError = $"命令: {CommandName} {this._lastRunArgs}\r\n退出代码: {exitCode}\r\n\r\n可能的原因:\r\n{hints}\r\n\r\n完整输出:\r\n{output}";
            this.AppendLog($"[启动失败,进程已退出,代码 {exitCode}]\r\n");
            return true;
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
            if (!process.HasExited)
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

        /// <summary>
        /// 分析启动失败的输出,识别常见错误模式并给出可操作的建议。
        /// </summary>
        private static string AnalyzeFailureOutput(string output)
        {
            var hints = new List<string>();

            // 端口被占用(EADDRINUSE / Windows 套接字提示),尝试提取端口号
            var portMatch = Regex.Match(
                output,
                @"EADDRINUSE[^0-9]*(\d+)|address already in use[^0-9]*(\d+)|套接字地址[^0-9]*(\d+)",
                RegexOptions.IgnoreCase);
            if (portMatch.Success)
            {
                var port = portMatch.Groups.Values.Skip(1).FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value;
                if (!string.IsNullOrEmpty(port))
                {
                    hints.Add($"端口 {port} 已被占用:可能已有一个 dsh 实例正在运行。"
                        + "可先点击\"停止\"按钮,或在任务管理器中结束旧的 node.exe 进程。"
                        + $"\r\n  也可运行 netstat -ano | findstr :{port} 查找占用该端口的进程(末列为 PID)。"
                        + "\r\n  如需换端口,可在首页展开项里的“端口”输入框中改成一个空闲端口后重新运行。");
                }
            }

            // 全局包损坏/缺失
            if (Regex.IsMatch(output, @"Cannot find module|MODULE_NOT_FOUND", RegexOptions.IgnoreCase))
            {
                hints.Add("缺少模块:全局包可能损坏或安装不完整。"
                    + $"\r\n  建议在命令行执行 npm uninstall -g {PackageName} 后重新 npm install -g {PackageName}。");
            }

            // Node 版本不满足
            if (Regex.IsMatch(output, @"Unsupported engine|requires Node|Node\.js v?\d+.*required", RegexOptions.IgnoreCase))
            {
                hints.Add("Node.js 版本不满足该包要求:请升级 Node.js 到包要求的版本后重试。");
            }

            // 权限问题
            if (Regex.IsMatch(output, @"EACCES|Access is denied|拒绝访问|EPERM", RegexOptions.IgnoreCase))
            {
                hints.Add("权限不足:请尝试以管理员身份运行 DSH Launcher,或检查相关文件/端口的安全策略。");
            }

            // 配置文件损坏
            if (Regex.IsMatch(output, @"Unexpected token|SyntaxError.*JSON|Failed to parse", RegexOptions.IgnoreCase))
            {
                hints.Add("配置文件可能损坏或格式错误:请检查 dsh 的配置文件(通常是 JSON)是否合法。");
            }

            if (hints.Count == 0)
            {
                hints.Add("未识别到常见错误模式,请查看下方完整输出定位问题。");
            }

            return string.Join("\r\n", hints.Select(h => "• " + h));
        }

        /// <summary>
        /// 停止 dsh 进程(连同子进程树),并关闭 WebView 窗口。
        /// 注意:重启走 <see cref="RestartAsync"/>,不经过这里,因此重启时 WebView 窗口会保留下来复用。
        /// </summary>
        public void Stop()
        {
            this._stopRequestedByUser = true;

            var process = this._process;
            if (process is not null && !process.HasExited)
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
