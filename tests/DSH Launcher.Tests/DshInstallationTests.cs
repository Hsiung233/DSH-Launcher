using System;
using System.IO;
using System.Linq;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// "dsh 是谁装的、装的什么版本"的纯逻辑。
/// <para>
/// 这块原先只认 npm(<c>npm ls -g</c>),于是 pnpm 全局安装的机器上界面长期显示"未安装" ——
/// 两个管理器的全局目录是两套,而 dsh 的 shim 在 PATH 上长得一模一样。
/// 现在检测不依赖具体管理器:先 <c>dsh --version</c>,再回退到两家的全局清单;
/// 安装来源只用来决定"更新时敲哪条命令"。
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
    [DataRow("C:\\Users\\x\\AppData\\Local\\pnpm\\global\\v11 (PRIVATE)\n│\n└── @deepseek-ai/dsh@0.2.1-rc.4\n", "0.2.1-rc.4")]
    [DataRow("/usr/local/lib\n+-- @deepseek-ai/dsh@1.0.0\n", "1.0.0")]
    [DataRow("└── @deepseek-ai/dsh-hmr@1.2.0\n", null)]  // 同 scope 的其它包不能命中
    [DataRow("(empty)\n", null)]
    [DataRow("", null)]
    public void ParseGlobalListVersion_ReadsTreeOutputOfBothManagers(string? output, string? expected)
    {
        Assert.AreEqual(expected, DshCli.ParseGlobalListVersion(output, "@deepseek-ai/dsh"));
    }

    [DataTestMethod]
    [DataRow(@"C:\Users\x\AppData\Local\pnpm\bin\dsh.cmd", @"C:\Users\x\AppData\Local\pnpm\bin", 2)]
    [DataRow(@"C:\Users\x\AppData\Local\pnpm\dsh.cmd", @"C:\Users\x\AppData\Local\pnpm", 2)]
    [DataRow(@"C:\Users\x\AppData\Roaming\npm\dsh.cmd", "", 1)]
    [DataRow(@"C:\Program Files\nodejs\dsh.cmd", "", 1)]
    [DataRow(@"C:\Users\x\.npm-global\bin\dsh.cmd", "", 1)]
    [DataRow(@"C:\Users\x\scoop\shims\dsh.cmd", "", 0)]
    [DataRow("/home/x/.local/share/pnpm/dsh", "", 2)]
    [DataRow("/home/x/.npm-global/bin/dsh", "", 1)]
    [DataRow("/usr/local/bin/dsh", "", 0)]
    [DataRow("", "", 0)]
    [DataRow(null, "", 0)]
    public void DetectInstaller_UsesShimDirectory(string? shimPath, string pnpmDir, int expected)
    {
        var pnpmDirs = pnpmDir.Length > 0 ? new[] { pnpmDir } : Array.Empty<string>();

        Assert.AreEqual((DshInstaller)expected, DshCli.DetectInstaller(shimPath, pnpmDirs));
    }

    [TestMethod]
    public void DetectInstaller_CustomPnpmHomeIsMatchedByDirectoryList()
    {
        // PNPM_HOME 被改成不含 "pnpm" 字样时,只能靠调用方给出的目录表精确比对
        Assert.AreEqual(
            DshInstaller.Pnpm,
            DshCli.DetectInstaller(@"D:\tools\shims\dsh.cmd", new[] { @"D:\tools\shims" }));
        Assert.AreEqual(
            DshInstaller.Unknown,
            DshCli.DetectInstaller(@"D:\tools\shims\dsh.cmd", Array.Empty<string>()));
    }

    [TestMethod]
    public void BuildKnownBinDirectories_WindowsCoversPnpmAndNpmGlobalBins()
    {
        var localAppData = @"C:\Users\x\AppData\Local";
        var roamingAppData = @"C:\Users\x\AppData\Roaming";

        var dirs = DshCli.BuildKnownBinDirectories(
            isWindows: true,
            pnpmHome: Path.Combine(localAppData, "pnpm"),
            localAppData: localAppData,
            roamingAppData: roamingAppData,
            programFiles: @"C:\Program Files",
            userProfile: @"C:\Users\x",
            home: null);

        // pnpm 的 shim 在 PNPM_HOME\bin,npm 的在 %APPDATA%\npm
        CollectionAssert.Contains(dirs.ToList(), Path.Combine(localAppData, "pnpm", "bin"));
        CollectionAssert.Contains(dirs.ToList(), Path.Combine(roamingAppData, "npm"));
        CollectionAssert.Contains(dirs.ToList(), Path.Combine(@"C:\Program Files", "nodejs"));

        // 即使 PNPM_HOME 与 %LOCALAPPDATA%\pnpm 是同一个目录,候选表也只留一份
        Assert.AreEqual(
            1,
            dirs.Count(dir => string.Equals(dir, Path.Combine(localAppData, "pnpm"), StringComparison.OrdinalIgnoreCase)));

        // pnpm 优先于 npm:同名 shim 两边都有时,先命中的那个才决定"谁装的"
        Assert.IsTrue(
            dirs.ToList().FindIndex(dir => dir.EndsWith("pnpm\\bin", StringComparison.OrdinalIgnoreCase))
            < dirs.ToList().FindIndex(dir => dir.Equals(Path.Combine(roamingAppData, "npm"), StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void BuildKnownBinDirectories_UnixUsesPosixLocations()
    {
        var dirs = DshCli.BuildKnownBinDirectories(
            isWindows: false,
            pnpmHome: "/home/x/.local/share/pnpm",
            localAppData: null,
            roamingAppData: null,
            programFiles: null,
            userProfile: null,
            home: "/home/x");

        CollectionAssert.Contains(dirs.ToList(), "/home/x/.local/share/pnpm");
        CollectionAssert.Contains(dirs.ToList(), "/opt/homebrew/bin");
        CollectionAssert.Contains(dirs.ToList(), "/usr/local/bin");
        CollectionAssert.DoesNotContain(dirs.ToList(), "/home/x/AppData/Roaming/npm");
    }

    [TestMethod]
    public void InstallerDescription_IsUserFacing()
    {
        Assert.AreEqual("npm 全局安装", DshInstaller.Npm.Describe());
        Assert.AreEqual("pnpm 全局安装", DshInstaller.Pnpm.Describe());
        Assert.AreEqual("安装来源未知", DshInstaller.Unknown.Describe());
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow(@"C:\Program Files\nodejs\dsh.cmd")]
    [DataRow("/usr/local/bin/dsh")]
    public void ResolveNodePathFor_NonPnpmGlobalShimAddsNothing(string shimPath)
    {
        // 只有 pnpm 全局 bin 下的 shim 需要补 NODE_PATH;npm / nodejs 安装的 hoisted 布局自己能找到可选平台包
        Assert.AreEqual(string.Empty, DshCli.ResolveNodePathFor(shimPath));
    }

    /// <summary>
    /// 用一个临时目录搭出 pnpm 全局布局(shim → global/v11/&lt;hash&gt; → node_modules),验证:
    /// ① 能从 shim 内容里找到项目目录(不依赖 PNPM_HOME、不写死 hash);
    /// ② 补齐的是项目根、.pnpm\node_modules 与 dsh 包自身的目录。
    /// 这正是 pnpm 升级后 hash/版本目录变化时仍能自动跟上的依据。
    /// </summary>
    [TestMethod]
    public void ResolveNodePathFor_PnpmGlobalShim_FollowsShimToProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-nodepath-" + Guid.NewGuid().ToString("N"));
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var pnpmHome = Path.Combine(root, "pnpm");
            var versionDir = isWindows ? "v11" : "v5";
            var hash = "abc123def456";

            // Windows:shim 在 PNPM_HOME\bin,global 在 PNPM_HOME\global;类 Unix:shim 就在 PNPM_HOME 下
            var binDir = isWindows ? Path.Combine(pnpmHome, "bin") : pnpmHome;
            var projectDir = Path.Combine(pnpmHome, "global", versionDir, hash);
            var projectModules = Path.Combine(projectDir, "node_modules");
            var hoistDir = Path.Combine(projectModules, ".pnpm", "node_modules");
            var dshDir = Path.Combine(projectModules, "@deepseek-ai", "dsh");

            Directory.CreateDirectory(hoistDir);
            Directory.CreateDirectory(Path.Combine(dshDir, "node_modules"));
            Directory.CreateDirectory(binDir);

            var shimTarget = string.Join(
                Path.DirectorySeparatorChar,
                "global",
                versionDir,
                hash,
                "node_modules",
                "@deepseek-ai",
                "dsh",
                "lib",
                "bin.js");
            var shimPath = Path.Combine(binDir, isWindows ? "dsh.cmd" : "dsh");
            File.WriteAllText(
                shimPath,
                isWindows
                    ? $"@SETLOCAL\r\nnode \"%~dp0\\..\\{shimTarget}\" %*\r\n"
                    : $"#!/bin/sh\nexec node \"{Path.Combine(pnpmHome, shimTarget)}\" \"$@\"\n");

            var entries = DshCli.ResolveNodePathFor(shimPath)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            CollectionAssert.Contains(entries, projectModules);
            CollectionAssert.Contains(entries, hoistDir);
            CollectionAssert.Contains(entries, Path.Combine(dshDir, "node_modules"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响断言结果
            }
        }
    }
}
