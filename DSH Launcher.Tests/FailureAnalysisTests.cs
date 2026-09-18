using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 启动失败的输出分析。
/// 这是"用户看到失败弹窗之后能不能自己解决"的全部依据:识别错了不会崩,
/// 只会把人引向错误的排查方向(例如明明是端口占用却提示重装全局包)。
/// </summary>
[TestClass]
public sealed class FailureAnalysisTests
{
    private const string PackageName = "@deepseek-ai/dsh";

    [TestMethod]
    public void AnalyzeFailureOutput_AddressInUse_NamesThePortAndNextSteps()
    {
        var hints = StartFailureDiagnostics.AnalyzeFailureOutput(
            "Error: listen EADDRINUSE: address already in use 127.0.0.1:3080", PackageName);

        StringAssert.Contains(hints, "端口 3080 已被占用");
        StringAssert.Contains(hints, "netstat -ano | findstr :3080");
    }

    [TestMethod]
    public void AnalyzeFailureOutput_ChineseAddressInUseMessage_AlsoMatches()
    {
        var hints = StartFailureDiagnostics.AnalyzeFailureOutput("通常每个套接字地址 3080 只允许使用一次", PackageName);

        StringAssert.Contains(hints, "端口 3080 已被占用");
    }

    [TestMethod]
    public void AnalyzeFailureOutput_MissingModule_SuggestsReinstall()
    {
        var hints = StartFailureDiagnostics.AnalyzeFailureOutput(
            "Error: Cannot find module '@deepseek-ai/dsh'\n    at Module._resolveFilename", PackageName);

        StringAssert.Contains(hints, "缺少模块");
        StringAssert.Contains(hints, PackageName);
    }

    [TestMethod]
    public void AnalyzeFailureOutput_UnsupportedEngine_MentionsNodeVersion()
    {
        var hints = StartFailureDiagnostics.AnalyzeFailureOutput(
            "npm ERR! Unsupported engine: wanted {\"node\":\">=20\"}", PackageName);

        StringAssert.Contains(hints, "Node.js 版本不满足");
    }

    [TestMethod]
    public void AnalyzeFailureOutput_AccessDenied_MentionsPermissions()
    {
        StringAssert.Contains(
            StartFailureDiagnostics.AnalyzeFailureOutput("Error: EACCES: permission denied", PackageName),
            "权限不足");
        StringAssert.Contains(
            StartFailureDiagnostics.AnalyzeFailureOutput("拒绝访问。", PackageName),
            "权限不足");
    }

    [TestMethod]
    public void AnalyzeFailureOutput_UnrecognizedOutput_TellsUserToReadFullLog()
    {
        var hints = StartFailureDiagnostics.AnalyzeFailureOutput("something nobody has seen before", PackageName);

        StringAssert.Contains(hints, "未识别到常见错误模式");
    }

    [TestMethod]
    public void AnalyzeFailureOutput_ReturnsBulletList()
    {
        var hints = StartFailureDiagnostics.AnalyzeFailureOutput("EADDRINUSE 3080", PackageName);

        StringAssert.StartsWith(hints, "• ");
    }

    [DataTestMethod]
    [DataRow("Cannot find package '@deepseek-ai/dsh-client-ui'", true)]
    [DataRow("Error [ERR_MODULE_NOT_FOUND]: Cannot find module", true)]
    [DataRow("Error: listen EADDRINUSE 3080", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsProfileResolutionFailure_MatchesOnlyTheKnownSignature(string? output, bool expected)
    {
        Assert.AreEqual(expected, StartFailureDiagnostics.IsProfileResolutionFailure(output));
    }

    [TestMethod]
    public void ProfileResolutionHintFor_ResolutionFailure_ExplainsTheCompatChain()
    {
        var hint = StartFailureDiagnostics.ProfileResolutionHintFor(
            "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@x/y' imported from C:\\Users\\nobody\\.dsh\\profiles\\web\\index.js");

        StringAssert.Contains(hint, "上游进程链");
        StringAssert.Contains(hint, "重新打开");
    }

    [TestMethod]
    public void ProfileResolutionHintFor_OtherFailures_AddsNothing()
    {
        Assert.AreEqual(string.Empty, StartFailureDiagnostics.ProfileResolutionHintFor("Error: listen EADDRINUSE 3080"));
        Assert.AreEqual(string.Empty, StartFailureDiagnostics.ProfileResolutionHintFor(string.Empty));
    }
}

