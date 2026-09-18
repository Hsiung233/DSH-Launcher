using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// <see cref="DshService"/> 的"启动失败诊断"接线部分。
    /// <para>
    /// 规则表与文件系统探测都在 <see cref="StartFailureDiagnostics"/> 里(那块是纯函数 + 只读探测,
    /// 可单独测试);这里只保留需要服务上下文的胶水:用到的包名/命令名、以及"让服务自己定位 shim"。
    /// </para>
    /// </summary>
    public sealed partial class DshService
    {
        /// <summary>分析失败输出,给出可操作建议(规则表见 <see cref="StartFailureDiagnostics"/>)。</summary>
        private static string AnalyzeFailureOutput(string output)
            => StartFailureDiagnostics.AnalyzeFailureOutput(output, PackageName);

        /// <summary>是否为"profile 依赖解析失败"(决定要不要走补齐依赖后的兜底重试)。</summary>
        private static bool IsProfileResolutionFailure(string? output)
            => StartFailureDiagnostics.IsProfileResolutionFailure(output);

        /// <summary>"profile 依赖解析失败"时追加的可操作提示。</summary>
        private static string ProfileResolutionHintFor(string output)
            => StartFailureDiagnostics.ProfileResolutionHintFor(output);

        /// <summary>
        /// 解析失败当刻拍依赖层快照写进 app.log。
        /// shim 路径由本服务定位后传入(定位逻辑属于命令执行,不属于诊断)。
        /// </summary>
        private static async Task CaptureResolutionDiagnosticsAsync(string failureOutput)
        {
            var shimPath = string.Empty;
            try
            {
                var (found, _) = await DshCli.FindAsync();
                shimPath = found;
            }
            catch (System.Exception)
            {
                // 定位失败时留空:快照里会记成"未定位到"
            }

            StartFailureDiagnostics.WriteSnapshot(failureOutput, ProfileName, shimPath);
        }
    }
}
