using System;
using System.Linq;
using System.Text.RegularExpressions;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// <see cref="LogBuffer"/> 的裁剪与读取语义。
/// 这块是"日志面板整段重排会把 UI 线程钉死"的直接对策(见类注释里的 43 万字符实测),
/// 裁剪一旦退化成"从中间切断/丢光缓冲区",界面会以最难复现的方式变卡或空白。
/// </summary>
[TestClass]
public sealed class LogBufferTests
{
    /// <summary>"…… 日志过长,已省略更早的 N 字符 ……" 里的 N。</summary>
    private static readonly Regex OmittedRegex = new(@"已省略更早的 (?<n>\d+) 字符");

    [TestMethod]
    public void Append_UnderLimit_KeepsEverythingWithoutNote()
    {
        var buffer = new LogBuffer(100);
        buffer.Append("hello\r\n");

        Assert.AreEqual("hello\r\n", buffer.ToString());
        Assert.AreEqual(7, buffer.Length);
    }

    [TestMethod]
    public void Append_OverLimit_DropsWholeLinesFromHeadAndReportsCount()
    {
        const int maxChars = 100;
        var line = "0123456789\r\n"; // 12 字符,长度整除上限,便于断言"整行"
        var buffer = new LogBuffer(maxChars);

        for (var i = 0; i < 20; i++)
        {
            buffer.Append(line);
        }

        var text = buffer.ToString();
        var retained = StripNote(text);

        // ① 保留下来的必须是完整行(绝不把一行切成两半)
        Assert.AreEqual(
            string.Concat(Enumerable.Repeat("0123456789\r\n", retained.Length / line.Length)),
            retained);
        Assert.AreEqual(0, retained.Length % line.Length);

        // ② 保留量在上限内,且确实裁掉了内容
        Assert.IsTrue(buffer.Length <= maxChars, $"保留 {buffer.Length} 字符,超过上限 {maxChars}");
        Assert.IsTrue(retained.Length > 0, "裁剪把缓冲区丢空了");

        // ③ 提示行报出的省略字符数 = 总输出 - 保留量(不能在裁剪后算错)
        var match = OmittedRegex.Match(text);
        Assert.IsTrue(match.Success, $"缺少省略提示:{text}");
        Assert.AreEqual(20 * line.Length - retained.Length, int.Parse(match.Groups["n"].Value));
    }

    [TestMethod]
    public void Append_OverLimit_KeepsTailNotHead()
    {
        var buffer = new LogBuffer(64);
        buffer.Append(new string('a', 200) + "\r\n");
        buffer.Append("tail-marker\r\n");

        StringAssert.Contains(buffer.ToString(), "tail-marker");
    }

    [TestMethod]
    public void ReadFrom_MarkInsideBuffer_ReturnsOnlyNewerText()
    {
        var buffer = new LogBuffer(1000);
        buffer.Append("first\r\n");
        var mark = buffer.Length;
        buffer.Append("second\r\n");

        Assert.AreEqual("second\r\n", buffer.ReadFrom(mark));
    }

    [TestMethod]
    public void ReadFrom_OutOfRangeMark_IsClampedInsteadOfThrowing()
    {
        var buffer = new LogBuffer(1000);
        buffer.Append("abc\r\n");

        // 调用方(启动失败留证)可能拿着已被裁掉的下标,或与「清空」并发 —— 都不能抛
        Assert.AreEqual(string.Empty, buffer.ReadFrom(9999));
        Assert.AreEqual("abc\r\n", buffer.ReadFrom(-5));
    }

    [TestMethod]
    public void Clear_ResetsOmittedCounter()
    {
        var buffer = new LogBuffer(32);
        buffer.Append(new string('x', 200));
        Assert.IsTrue(buffer.Length > 0);

        buffer.Clear();

        Assert.AreEqual(string.Empty, buffer.ToString());
        Assert.AreEqual(0, buffer.Length);

        // 清空后再写:不应残留"已省略"提示(计数器没归零就会出现假象)
        buffer.Append("fresh\r\n");
        Assert.AreEqual("fresh\r\n", buffer.ToString());
    }

    /// <summary>去掉开头那行"已省略"提示,返回真正保留的正文。</summary>
    private static string StripNote(string text)
    {
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return text.StartsWith("……", StringComparison.Ordinal) && end >= 0
            ? text[(end + 2)..]
            : text;
    }
}
