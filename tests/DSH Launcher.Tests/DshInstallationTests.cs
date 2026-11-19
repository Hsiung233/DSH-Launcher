using System.IO;
using System.Linq;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// "dsh 装了什么版本"与"去哪儿找 dsh 命令"的纯逻辑。
/// <para>
/// 安装方式只支持 npm 全局安装(见 <see cref="DshCli"/>)。pnpm 全局安装的探测、
/// <c>NODE_PATH</c> 补齐与安装来源判定都已随 pnpm 支持一起移除,
/// 这里对应地删掉了那几组用例,只保留 npm 侧的解析与定位规则。
/// </para>
/// </summary>
[TestClass]
public sealed class DshInstallationTests
{
    [DataTestMethod]
    [DataRow("0.2.1-rc.4", "0.2.1-rc.4")]
    [DataRow("v1.2.3", "1.2.3")]
    [DataRow("  1.2.3  ", "1.2.3")]
    [DataRow("1.2.3\r", "1.2.3")]                       // Windows 下按行切分会留下 \r
    [DataRow("0.2.1+build.5", "0.2.1+build.5")]
    [DataRow("Node.js v20.11.0\n0.2.1", "0.2.1")]       // 带前缀的 Node 版本不能被当成 dsh 版本
    [DataRow("npm error code ENOENT", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void ParseVersionOutput_TakesWholeLineVersionOnly(string? output, string? expected)
    {
        Assert.AreEqual(expected, DshCli.ParseVersionOutput(output));
    }

    [DataTestMethod]
    [DataRow("C:\\Users\\x\\AppData\\Roaming\\npm\n`-- @deepseek-ai/dsh@0.2.1\n", "0.2.1")]
    [DataRow("/usr/local/lib\n+-- @deepseek-ai/dsh@1.0.0\n", "1.0.0")]
    [DataRow("└── @deepseek-ai/dsh-hmr@1.2.0\n", null)]  // 同 scope 的其它包不能命中
    [DataRow("(empty)\n", null)]
    [DataRow("", null)]
    public void ParseGlobalListVersion_ReadsNpmTreeOutput(string? output, string? expected)
    {
        Assert.AreEqual(expected, DshCli.ParseGlobalListVersion(output, "@deepseek-ai/dsh"));
    }

    [TestMethod]
    public void BuildKnownBinDirectories_WindowsCoversNpmGlobalBins()
    {
        var roamingAppData = @"C:\Users\x\AppData\Roaming";
        var localAppData = @"C:\Users\x\AppData\Local";

        var dirs = DshCli.BuildKnownBinDirectories(
            isWindows: true,
            roamingAppData: roamingAppData,
            programFiles: @"C:\Program Files",
            userProfile: @"C:\Users\x",
            home: null);

        // npm 的全局 bin 在 %APPDATA%\npm;node MSI 安装时是 %ProgramFiles%\nodejs
        CollectionAssert.Contains(dirs.ToList(), Path.Combine(roamingAppData, "npm"));
        CollectionAssert.Contains(dirs.ToList(), Path.Combine(@"C:\Users\x", "AppData", "Roaming", "npm"));
        CollectionAssert.Contains(dirs.ToList(), Path.Combine(@"C:\Program Files", "nodejs"));

        // pnpm 支持已移除:它的全局 bin 目录不该再出现在候选表里
        CollectionAssert.DoesNotContain(dirs.ToList(), Path.Combine(localAppData, "pnpm", "bin"));
        CollectionAssert.DoesNotContain(dirs.ToList(), Path.Combine(localAppData, "pnpm"));
    }

    [TestMethod]
    public void BuildKnownBinDirectories_UnixUsesPosixLocations()
    {
        var dirs = DshCli.BuildKnownBinDirectories(
            isWindows: false,
            roamingAppData: null,
            programFiles: null,
            userProfile: null,
            home: "/home/x");

        // 用 Path.Combine 拼期望值:类 Unix 目录在 Windows 上跑测试时会被拼成反斜杠,
        // 直接写 "/home/x/.npm-global/bin" 这种字面量会因分隔符不同而失败
        CollectionAssert.Contains(dirs.ToList(), Path.Combine("/home/x", ".npm-global", "bin"));
        CollectionAssert.Contains(dirs.ToList(), "/opt/homebrew/bin");
        CollectionAssert.Contains(dirs.ToList(), "/usr/local/bin");
        CollectionAssert.DoesNotContain(dirs.ToList(), "/home/x/.scoop/shims");

        // pnpm 支持已移除:类 Unix 的 pnpm 全局 bin 不再兜底
        CollectionAssert.DoesNotContain(dirs.ToList(), Path.Combine("/home/x", ".local", "share", "pnpm"));
        CollectionAssert.DoesNotContain(dirs.ToList(), Path.Combine("/home/x", "Library", "pnpm"));
    }
}
