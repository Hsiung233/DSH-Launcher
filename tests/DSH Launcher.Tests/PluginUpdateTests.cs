using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 插件更新检测的纯逻辑:是否需要更新的判定 + <c>npm view</c> 输出的解析。
/// npm 输出形态不受控(告警行、引号、空输出),判错的表现是"永远提示可更新"或"永远不提示"。
/// </summary>
[TestClass]
public sealed class PluginUpdateTests
{
    [DataTestMethod]
    [DataRow("1.2.3", "1.2.4", true)]
    [DataRow("1.2.4", "1.2.3", false)]   // 本地比 npm 新(本地链接/降级安装)不算可更新
    [DataRow("1.2.3", "1.2.3", false)]
    [DataRow("1.2.3", "1.10.0", true)]   // 按数字比,不是按字符串比
    [DataRow("1.0.0", "1.0.0-beta", false)]  // npm 上的预发布不算更新
    [DataRow("1.0.0-beta", "1.0.0", true)]   // 本地是预发布、npm 有正式版 → 可更新
    [DataRow("v1.2.3", "1.2.4", true)]   // 本地版本带 v 前缀
    [DataRow("1.2.3", "v1.2.4", true)]   // npm 输出带 v 前缀
    public void IsUpdateNeeded_ComparesSemver(string installed, string latest, bool expected)
    {
        Assert.AreEqual(expected, PluginService.IsUpdateNeeded(installed, latest), $"{installed} vs {latest}");
    }

    [DataTestMethod]
    [DataRow(null, "1.2.4")]
    [DataRow("1.2.3", null)]
    [DataRow("", "1.2.4")]
    [DataRow("1.2.3", "")]
    [DataRow("   ", "   ")]
    public void IsUpdateNeeded_MissingVersions_ReturnsFalse(string? installed, string? latest)
    {
        // "未知就当作无更新":查不到版本宁可少提示,也不误报
        Assert.IsFalse(PluginService.IsUpdateNeeded(installed, latest));
    }

    [TestMethod]
    public void ParseLatestVersionOutput_TakesLastNonEmptyLine()
    {
        var stdout = "npm warn config Some noise\r\n1.2.4\r\n";
        Assert.AreEqual("1.2.4", PluginService.ParseLatestVersionOutput(stdout));
    }

    [TestMethod]
    public void ParseLatestVersionOutput_StripsQuotes()
    {
        Assert.AreEqual("1.2.4", PluginService.ParseLatestVersionOutput("\"1.2.4\""));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   \r\n  \n")]
    [DataRow(null)]
    public void ParseLatestVersionOutput_Empty_ReturnsNull(string? stdout)
    {
        Assert.IsNull(PluginService.ParseLatestVersionOutput(stdout ?? string.Empty));
    }
}
