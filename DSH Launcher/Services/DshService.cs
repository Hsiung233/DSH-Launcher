using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const string RunArgs = "web --no-open";

        private Process? _process;
        private readonly StringBuilder _log = new();
        private DateTime _startTimeUtc = DateTime.MinValue;
        private int _lastStartLogMark;
        private volatile bool _stopRequestedByUser;
        private bool _startFailureHandled;

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

        /// <summary>清空日志并通知 UI。</summary>
        public void ClearLog()
        {
            this._log.Clear();
            this.WebUrl = null;
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
            this.AppendLog($"> {CommandName} {RunArgs}\r\n");

            // 1) 先定位 npm 全局 bin 下的 shim,给出明确错误,避免“进程活着但命令没跑起来”的模糊状态
            string shimPath;
            try
            {
                var (whereOut, whereErr) = await RunCaptureAsync($"where {CommandName}");
                shimPath = whereOut
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                            || line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? string.Empty;
                if (shimPath.Length == 0)
                {
                    this.LastStartError = $"命令: {CommandName} {RunArgs}\r\n\r\n未找到“{CommandName}”命令。"
                        + "npm 全局 bin 目录可能不在 PATH 中,或包未正确安装。"
                        + $"\r\n\r\nwhere 输出:\r\n{whereOut}{whereErr}".TrimEnd();
                    this.AppendLog("[启动失败] 未找到 dsh 命令\r\n");
                    return false;
                }
            }
            catch (Exception ex)
            {
                this.LastStartError = $"命令: {CommandName} {RunArgs}\r\n\r\n定位命令失败:\r\n{ex}";
                this.AppendLog($"[启动失败] 定位命令失败: {ex.Message}\r\n");
                return false;
            }

            // 2) 用完整路径通过 cmd 启动:不依赖 PATH;call 确保 .cmd shim 被正确执行且 cmd 等待其结束
            Process process;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c call \"{shimPath}\" {RunArgs}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        this.AppendLog(e.Data + "\r\n", detectWebUrl: true);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        this.AppendLog(e.Data + "\r\n", detectWebUrl: true);
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
                this.LastStartError = $"命令: {CommandName} {RunArgs}\r\n\r\n无法启动进程:\r\n{ex}";
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
            this.LastStartError = $"命令: {CommandName} {RunArgs}\r\n退出代码: {exitCode}\r\n\r\n可能的原因:\r\n{hints}\r\n\r\n完整输出:\r\n{output}";
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
                        + "\r\n  如需换端口,可在 dsh 的配置中修改监听端口。");
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

        /// <summary>停止 dsh 进程(连同子进程树)。</summary>
        public void Stop()
        {
            this._stopRequestedByUser = true;
            var process = this._process;
            if (process is null || process.HasExited)
            {
                return;
            }

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

        private static async Task<(string Stdout, string Stderr)> RunCaptureAsync(string command)
        {
            using var process = StartHidden(command);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (await stdout, await stderr);
        }

        /// <summary>运行命令并把 stdout/stderr 流式追加到日志,进程退出后返回。</summary>
        private async Task RunStreamingAsync(string command)
        {
            // npm install 流程(npm 输出不含 Web 地址)与之前相同,无需代码变更
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = StartHidden(command);
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    this.AppendLog(e.Data + "\r\n");
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    this.AppendLog(e.Data + "\r\n");
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

        private static Process StartHidden(string command)
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        }

        [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):\d+(?:/[^\s""'<>]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
        private static partial Regex GetWebUrlRegex();
    }
}
