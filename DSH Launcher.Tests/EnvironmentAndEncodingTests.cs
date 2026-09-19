using System.Globalization;
using System.Text;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 环境设置的归一化,以及子进程输出的编码判定。
/// 后者是"一次 cmd.exe 输出里混着 UTF-8 与 OEM 代码页"的对策(实测中文 Windows 上是 GBK):
/// 判错的表现是界面里整片乱码,而它只在中文环境下出错,英文环境下测不出来。
/// </summary>
[TestClass]
public sealed class EnvironmentAndEncodingTests
{
    [DataTestMethod]
    [DataRow(null, "")]
    [DataRow("", "")]
    [DataRow("   ", "")]
    [DataRow("127.0.0.1:7890", "http://127.0.0.1:7890")]          // 缺协议时补 http://
    [DataRow("http://127.0.0.1:7890", "http://127.0.0.1:7890")]
    [DataRow("http://127.0.0.1:7890/", "http://127.0.0.1:7890")]  // 去掉末尾斜杠
    [DataRow("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [DataRow("http：//127.0.0.1：7890", "http://127.0.0.1:7890")]  // 中文输入法的全角冒号
    [DataRow("http://a／b", "http://a/b")]                        // 全角斜杠
    public void NormalizeProxyUrl_ProducesUsableProxyUrl(string? raw, string expected)
    {
        Assert.AreEqual(expected, ChildEnvironment.NormalizeProxyUrl(raw));
    }

    [DataTestMethod]
    [DataRow(null, "")]
    [DataRow("", "")]
    [DataRow("a", "a")]
    [DataRow("a, b", "a,b")]
    [DataRow("a;b;;c", "a,b,c")]
    [DataRow("a，b", "a,b")]              // 全角逗号
    [DataRow(" a \t b ", "a,b")]
    [DataRow("localhost,127.0.0.1,.corp.com", "localhost,127.0.0.1,.corp.com")]
    public void NormalizeNoProxy_SplitsAndJoinsConsistently(string? raw, string expected)
    {
        Assert.AreEqual(expected, ChildEnvironment.NormalizeNoProxy(raw));
    }

    [TestMethod]
    public void DecodeChildOutputLine_Utf8Bytes_RoundTrip()
    {
        const string text = "中文与符号 — ✓ ok";

        Assert.AreEqual(text, PlatformProcess.DecodeChildOutputLine(AsRawByteChars(Encoding.UTF8.GetBytes(text))));
    }

    [TestMethod]
    public void DecodeChildOutputLine_Ascii_IsUnaffected()
    {
        const string text = "npm WARN deprecated foo@1.0.0";

        Assert.AreEqual(text, PlatformProcess.DecodeChildOutputLine(text));
    }

    [TestMethod]
    public void DecodeChildOutputLine_EmptyStaysEmpty()
    {
        Assert.AreEqual(string.Empty, PlatformProcess.DecodeChildOutputLine(string.Empty));
    }

    [TestMethod]
    public void DecodeChildOutputText_DecidesPerLine()
    {
        // 逐行判定:同一份捕获里既有 Node 的 UTF-8 输出,也可能有 cmd 自己的消息
        var text = "第一行\nsecond line\n第三行";

        Assert.AreEqual(text, PlatformProcess.DecodeChildOutputText(AsRawByteChars(Encoding.UTF8.GetBytes(text))));
    }

    [TestMethod]
    public void DecodeChildOutputLine_InvalidUtf8_FallsBackToOemCodePage()
    {
        // 固定到 zh-CN:OEM 代码页随系统语言变化(英文机 CP437 / 中文机 GBK 936),
        // 不固定的话该用例在英文 CI 上要么误判要么跳过;显式设置让任何机器走同一条路。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        try
        {
            RunOemFallbackAssertion();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static void RunOemFallbackAssertion()
    {
        // cmd.exe 自己的消息(如「'xxx' 不是内部或外部命令」)是系统 OEM 代码页。
        // 用当前机器的 OEM 代码页造出同样的字节,要求解码后还原成原文。
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        const string text = "错误:拒绝访问";
        var bytes = oem.GetBytes(text);

        // 前提检查 1:OEM 代码页必须能**无损表示**这条用例文本 —— 若表示不了,
        // GetBytes 会把中文替换成 '?',造出来的字节根本不是真实 cmd 输出的样子。
        if (oem.GetString(bytes) != text)
        {
            Assert.Inconclusive($"当前 OEM 代码页 {oem.CodePage} 无法无损表示这条用例文本,跳过");
        }

        // 前提检查 2:这些字节必须是**非法 UTF-8**,否则走的不是"回退 OEM"分支。
        if (Encoding.UTF8.GetString(bytes) == text)
        {
            Assert.Inconclusive($"当前 OEM 代码页 {oem.CodePage} 在这条用例上不构成非法 UTF-8,跳过");
        }

        Assert.AreEqual(text, PlatformProcess.DecodeChildOutputLine(AsRawByteChars(bytes)));
    }

    /// <summary>把原始字节还原成 <see cref="PlatformProcess"/> 约定接收的"原样字节"字符串(Latin1)。</summary>
    private static string AsRawByteChars(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
