using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using DSH_Launcher.Models;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 策展目录 JSON 的解析:把两份结构完全不同的社区目录归一成 <see cref="PluginCatalog"/>。
    /// <para>
    /// 为什么单独成类:这是**纯数据映射**(JsonElement → 模型),不碰网络、不碰文件、不碰界面;
    /// 而两份目录的字段体系完全不同(完整单词 vs 缩写),是最容易写错、也最值得单独看的一块。
    /// 拉取与缓存仍由 <see cref="PluginService"/> 负责 —— 那边决定"什么时候拿、要不要用缓存"。
    /// </para>
    /// </summary>
    internal static partial class PluginCatalogReader
    {
        /// <summary>
        /// 解析目录 JSON。**自动识别两份社区目录的结构**:
        /// 顶层是对象且带 <c>plugins</c> → awesome-dsh-plugin(dsh-market 的数据源);
        /// 顶层是数组 → dsh-plugin.org(dsh-plugin-hub 的数据源,字段是缩写)。
        /// </summary>
        public static PluginCatalog Parse(JsonElement root, string sourceUrl)
            => root.ValueKind == JsonValueKind.Array
                ? ParseDshPluginOrgCatalog(root, sourceUrl)
                : ParseAwesomeDshPluginCatalog(root, sourceUrl);

        /// <summary>awesome-dsh-plugin 结构:顶层 <c>categories</c> + <c>plugins</c>,字段是完整单词。</summary>
        private static PluginCatalog ParseAwesomeDshPluginCatalog(JsonElement root, string sourceUrl)
        {
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in cats.EnumerateObject())
                {
                    var zh = ReadString(item.Value, "zh");
                    var en = ReadString(item.Value, "en");
                    categories[item.Name] = zh.Length > 0 ? zh : (en.Length > 0 ? en : item.Name);
                }
            }

            var entries = new List<PluginCatalogEntry>();
            if (root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in plugins.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var category = ReadString(item, "category");
                    entries.Add(new PluginCatalogEntry
                    {
                        Name = ReadString(item, "name"),
                        Owner = ReadString(item, "owner"),
                        RepoUrl = ReadString(item, "url"),
                        PageUrl = ReadString(item, "page"),
                        Category = category,
                        CategoryText = categories.TryGetValue(category, out var label) ? label : category,
                        DescriptionZh = ReadString(item, "description", "zh"),
                        DescriptionEn = ReadString(item, "description", "en"),
                        Npm = ReadString(item, "npm"),
                        Version = ReadString(item, "version"),
                        Stars = ReadInt(item, "stars"),
                        Downloads = ReadNullableInt(item, "downloads"),
                        Added = ReadString(item, "added"),
                        InstallSpec = ExtractInstallSpec(ReadString(item, "install")),
                        FallbackInstallSpec = ReadString(item, "tarball"),
                    });
                }
            }

            return new PluginCatalog
            {
                Entries = entries,
                Categories = categories,
                Updated = ReadString(root, "updated"),
                SourceUrl = sourceUrl,
                FetchedAtLocal = DateTime.Now,
            };
        }

        /// <summary>
        /// dsh-plugin.org 结构(dsh-plugin-hub 的数据源):顶层就是插件数组,字段是缩写 ——
        /// <c>n</c>=名字、<c>o</c>=作者、<c>c</c>=分类、<c>d</c>=描述、<c>r</c>={repo,npmPackage}、
        /// <c>ic</c>/<c>igc</c>=npm/GitHub 两条安装命令、<c>v</c>=验证状态、<c>sg</c>/<c>fk</c>=星标/Fork、
        /// <c>a</c>=收录日期、<c>vr</c>=版本(只有部分条目有)。
        /// 分类中文名不在数据里,取上游 <c>CATEGORY_LABELS</c> 的同一份映射。
        /// </summary>
        private static PluginCatalog ParseDshPluginOrgCatalog(JsonElement root, string sourceUrl)
        {
            var entries = new List<PluginCatalogEntry>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var owner = ReadString(item, "o");
                var name = ReadString(item, "n");
                var slug = ReadString(item, "s");
                if (slug.Length == 0)
                {
                    slug = name;
                }

                var repo = item.TryGetProperty("r", out var r) ? r : default;
                var repoPath = ReadString(repo, "repo");
                var npm = ReadString(repo, "npmPackage");
                var category = ReadString(item, "c");

                entries.Add(new PluginCatalogEntry
                {
                    Name = name,
                    Owner = owner,
                    RepoUrl = repoPath.Length > 0 ? "https://github.com/" + repoPath : string.Empty,
                    PageUrl = $"https://dsh-plugin.org/plugins/{owner}/{slug}",
                    Category = category,
                    CategoryText = DshPluginOrgCategories.TryGetValue(category, out var label) ? label : category,
                    // plugins.zh.json 的 d 字段本身就是中文
                    DescriptionZh = ReadString(item, "d"),
                    Npm = npm,
                    Version = ReadString(item, "vr").TrimStart('v', 'V'),
                    Stars = ReadInt(item, "sg"),
                    Downloads = null, // 该目录不提供下载量
                    Forks = ReadNullableInt(item, "fk"),
                    Verified = ReadString(item, "v").Equals("verified", StringComparison.OrdinalIgnoreCase),
                    Added = ReadString(item, "a"),
                    InstallSpec = ExtractInstallSpec(ReadString(item, "ic")),
                    FallbackInstallSpec = ExtractInstallSpec(ReadString(item, "igc")),
                });
            }

            return new PluginCatalog
            {
                Entries = entries,
                Categories = DshPluginOrgCategories,
                // 该目录顶层没有 updated 字段,不编造 —— 界面会退化成只显示拉取时间
                Updated = string.Empty,
                SourceUrl = sourceUrl,
                FetchedAtLocal = DateTime.Now,
            };
        }

        /// <summary>
        /// dsh-plugin.org 的分类中文名。数据文件里只有分类 id,中文名在上游
        /// <c>src/client/logic/constants.ts</c> 的 <c>CATEGORY_LABELS</c> 里,这里照抄同一份。
        /// </summary>
        private static readonly Dictionary<string, string> DshPluginOrgCategories = new(StringComparer.Ordinal)
        {
            ["interface"] = "界面与体验",
            ["session"] = "会话与消息",
            ["memory"] = "记忆与上下文",
            ["tools"] = "工具与能力",
            ["agent"] = "技能与智能体",
            ["workflow"] = "工作流与自动化",
            ["integration"] = "集成与连接",
            ["model"] = "模型与推理",
            ["dev"] = "开发与运维",
            ["knowledge"] = "数据与知识",
            ["fun"] = "娱乐",
        };

        /// <summary>
        /// 从目录给的 <c>dsh plugin --profile &lt;name&gt; add &lt;spec&gt;</c> 里取出 <c>&lt;spec&gt;</c>。
        /// 目录已经算好了首选来源(npm 包名优先,没有 npm 包时是 github:owner/repo)。
        /// </summary>
        private static string ExtractInstallSpec(string install)
        {
            if (install.Length == 0)
            {
                return string.Empty;
            }

            var match = InstallSpecRegex().Match(install);
            return match.Success ? match.Groups["spec"].Value.Trim() : install.Trim();
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        /// <summary>读嵌套字符串字段(如 description.zh)。</summary>
        private static string ReadString(JsonElement element, string name, string nested)
            => element.TryGetProperty(name, out var child) ? ReadString(child, nested) : string.Empty;

        private static int ReadInt(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return 0;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
        }

        /// <summary>读可空整数(目录里没有该项时返回 null,界面据此决定要不要显示)。</summary>
        private static int? ReadNullableInt(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        }

        /// <summary>从目录的 install 字段里取规格:<c>dsh plugin --profile web add &lt;spec&gt;</c>。</summary>
        [GeneratedRegex(@"\badd\s+(?<spec>\S.*?)\s*$")]
        private static partial Regex InstallSpecRegex();
    }
}
