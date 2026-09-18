using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 版本比较与"是否有更新"的判定。npm 上的版本号形态不受控(带 v 前缀、段数不齐、预发布标记),
/// 比较写错的表现是"永远提示有更新"或"永远不提示",都不会报错。
/// </summary>
[TestClass]
public sealed class VersionComparisonTests
{
    [DataTestMethod]
    [DataRow("1.2.3", "1.2.3", 0)]
    [DataRow("1.2.3", "1.2.4", -1)]
    [DataRow("1.2.4", "1.2.3", 1)]
    [DataRow("1.10.0", "1.9.0", 1)]      // 按数字比,不是按字符串比
    [DataRow("2.0.0", "10.0.0", -1)]
    [DataRow("1.2", "1.2.0", 0)]         // 段数不齐时缺的段按 0
    [DataRow("1.2.0.1", "1.2", 1)]
    [DataRow("v1.2.3", "1.2.3", 0)]      // 前缀 v 忽略
    [DataRow("V1.2.3", "1.2.3", 0)]
    [DataRow("1.0.0-beta", "1.0.0", -1)] // 预发布 < 正式
    [DataRow("1.0.0-beta", "1.0.0-alpha", 1)]
    [DataRow("1.0.0-beta", "1.0.0-beta", 0)]
    [DataRow("1.0.0-1", "1.0.0-2", -1)]
    public void CompareVersions_ReturnsExpectedSign(string a, string b, int expectedSign)
    {
        var actual = DshService.CompareVersions(a, b);

        Assert.AreEqual(expectedSign, System.Math.Sign(actual), $"{a} vs {b} => {actual}");
    }

    [TestMethod]
    public void CompareVersions_IgnoresSurroundingWhitespace()
    {
        Assert.AreEqual(0, DshService.CompareVersions(" 1.2.3 ", "1.2.3"));
    }
}
