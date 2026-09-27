using System;
using System.IO;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// <c>dsh --dump-config</c> 输出的解析与用户输入规格的归一化。
/// 解析器的关键约束是"只认顶层字段":<c>config:</c> 段里也有同名的 <c>name:</c>/<c>disabled:</c>,
/// 一旦被当成条目状态读走,插件页上每个插件的启停与版本显示都会错。
/// </summary>
[TestClass]
public sealed class CompositionDumpTests
{
    private const string Dump = """
    # == dsh 自带层
    - id: ui-schedule
      name: '@dsh/ui-schedule'
      disabled: false
      config:
        name: 不该被当成条目名
        disabled: true
    - id: quoted-plugin
      name: "double-quoted"
      disabled: true
    # == profile 层
    - id: 'it''s-odd'
      name: plain
    """;

    [TestMethod]
    public void ParseCompositionDump_TakesTopLevelFieldsAndSkipsConfigBlock()
    {
        var rows = PluginService.ParseCompositionDump(Dump);

        Assert.AreEqual(3, rows.Count);

        Assert.AreEqual("ui-schedule", rows[0].Id);
        Assert.AreEqual("@dsh/ui-schedule", rows[0].Name);
        Assert.IsFalse(rows[0].Disabled, "config 段里的 disabled: true 不该影响条目状态");

        Assert.AreEqual("quoted-plugin", rows[1].Id);
        Assert.AreEqual("double-quoted", rows[1].Name);
        Assert.IsTrue(rows[1].Disabled);
    }

    [TestMethod]
    public void ParseCompositionDump_UnquotesSingleQuotedIdWithEscapedQuote()
    {
        var rows = PluginService.ParseCompositionDump(Dump);

        Assert.AreEqual("it's-odd", rows[2].Id);
        Assert.AreEqual("plain", rows[2].Name);
        Assert.IsFalse(rows[2].Disabled);
    }

    [TestMethod]
    public void ParseCompositionDump_ListItemWithoutIdIsIgnored()
    {
        // 不是 Loader 条目的列表项(没有 id)拿不到启停入口,应当整条跳过而不是产生空行
        var rows = PluginService.ParseCompositionDump("- foo: bar\n  name: x\n");

        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public void ParseCompositionDump_EmptyOutputYieldsNoRows()
    {
        Assert.AreEqual(0, PluginService.ParseCompositionDump(string.Empty).Count);
        Assert.AreEqual(0, PluginService.ParseCompositionDump("# 只有注释\n").Count);
    }

    [DataTestMethod]
    [DataRow("", "")]
    [DataRow("   ", "")]
    [DataRow("pkg", "pkg")]
    [DataRow("@scope/pkg", "@scope/pkg")]
    [DataRow("@scope/pkg@^1.2.3", "@scope/pkg@^1.2.3")]
    [DataRow("github:owner/repo", "github:owner/repo")]
    [DataRow("https://example.com/a.tgz", "https://example.com/a.tgz")]
    public void NormalizeSpec_LeavesNonPathSpecsUntouched(string spec, string expected)
    {
        Assert.AreEqual(expected, PluginService.NormalizeSpec(spec));
    }

    [TestMethod]
    public void NormalizeSpec_TurnsRelativePathIntoAbsolutePath()
    {
        // dsh 会按"调用者的工作目录"解释相对路径,而启动器的 cwd 不确定 —— 必须转绝对路径
        var result = PluginService.NormalizeSpec("./local-plugin");

        Assert.IsTrue(Path.IsPathRooted(result), result);
        Assert.AreEqual(Path.GetFullPath("./local-plugin"), result);
    }

    [TestMethod]
    public void NormalizeSpec_KeepsFilePrefixWhileAbsolutizing()
    {
        var result = PluginService.NormalizeSpec("file:./local-plugin");

        StringAssert.StartsWith(result, "file:");
        Assert.IsTrue(Path.IsPathRooted(result["file:".Length..]), result);
    }

    [TestMethod]
    public void NormalizeSpec_ExpandsHomeShortcut()
    {
        var result = PluginService.NormalizeSpec("~/my-plugin");

        Assert.IsFalse(result.StartsWith('~'), result);
        Assert.IsTrue(Path.IsPathRooted(result), result);
        StringAssert.EndsWith(result, "my-plugin");
    }

    [TestMethod]
    public void NormalizeSpec_TrimsSurroundingWhitespace()
    {
        Assert.AreEqual("pkg", PluginService.NormalizeSpec("  pkg  "));
    }
}
