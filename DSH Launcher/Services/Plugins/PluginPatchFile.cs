using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DSH_Launcher.Services.Plugins
{
    /// <summary>
    /// profile 的 <c>cordis.patch.yml</c> 里"启动器托管区块"的读写。
    /// <para>
    /// 为什么单独成类:dsh 没有启停的官方接口(其 Web 设置页也把"写回启停"列为未做的后续工作),
    /// 启动器只能自己往补丁文件里写 id 定向的 <c>disabled</c> 覆盖。这段逻辑全是**文件格式知识**,
    /// 与插件清单/子进程那套没有关系,而且历史上踩过坑(见下面 <see cref="Write"/> 的注释),值得单独放。
    /// </para>
    /// <para>
    /// 线程与并发不在本类的职责内:调用方(<c>PluginService</c>)负责串行化与错误上报 ——
    /// 本类失败时**直接抛异常**,由调用方决定记哪条日志、返回什么。
    /// </para>
    /// </summary>
    internal static class PluginPatchFile
    {
        /// <summary>托管区块的开始标记(cordis.patch.yml 里以注释形式存在)。</summary>
        public const string ManagedBegin = "# >>> DSH Launcher 插件启停(以下区块由启动器维护,手动修改可能被覆盖)";

        /// <summary>托管区块的结束标记。</summary>
        public const string ManagedEnd = "# <<< DSH Launcher 插件启停";

        /// <summary>
        /// 读托管区块里的启停覆盖(id → disabled)。文件不存在/没有托管区块时返回空表。
        /// 读不出内容(IO 异常)时抛出,由调用方决定如何上报。
        /// </summary>
        public static Dictionary<string, bool> Read(string path)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return result;
            }

            var lines = SplitLines(File.ReadAllText(path));
            var (begin, end) = FindManagedRange(lines);
            if (begin < 0)
            {
                return result;
            }

            string? id = null;
            for (var i = begin + 1; i < end; i++)
            {
                var text = lines[i].Trim();

                if (text.StartsWith("- ", StringComparison.Ordinal))
                {
                    var item = text[2..].Trim();
                    id = item.StartsWith("id:", StringComparison.Ordinal) ? Unquote(item[3..].Trim()) : null;
                }
                else if (id is not null && text.StartsWith("disabled:", StringComparison.Ordinal))
                {
                    result[id] = text[9..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    id = null;
                }
            }

            return result;
        }

        /// <summary>
        /// 重写托管区块(逐行处理,只用注释行作边界,不动用户自己写的补丁条目)。
        ///
        /// ⚠ 关键约束:<c>[]</c> 本身就是一份完整的 YAML 文档,后面再追加条目会直接解析失败
        /// (实测 "end of the stream or a document separator is expected")。
        /// 所以有覆盖时必须**删掉**那行空数组标记,而不是在它下面追加。
        /// </summary>
        public static void Write(string path, IReadOnlyDictionary<string, bool> overrides)
        {
            var original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

            // 行尾风格跟着原文件走,避免把 LF 文件整体改成 CRLF
            var lineEnding = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = SplitLines(original);

            // ① 先摘掉上一次写的托管区块(含两行标记)
            var (begin, end) = FindManagedRange(lines);
            if (begin >= 0)
            {
                lines.RemoveRange(begin, end - begin + 1);
            }

            // ② 去掉尾部空行,让后面追加的内容紧贴已有内容
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            var hasUserEntries = lines.Any(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));

            // ③ 空数组标记本身就是一份完整的 YAML 文档:有覆盖时必须删掉它,
            //    否则在它后面追加条目会解析失败(见方法注释)
            if (!hasUserEntries)
            {
                lines.RemoveAll(line => line.Trim() == "[]");
            }

            if (overrides.Count == 0)
            {
                if (!hasUserEntries)
                {
                    lines.Add("[]");
                }
            }
            else
            {
                if (hasUserEntries)
                {
                    lines.Add(string.Empty); // 与用户自己写的条目之间留一个空行
                }

                lines.Add(ManagedBegin);
                foreach (var pair in overrides.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    lines.Add($"- id: {pair.Key}");
                    lines.Add($"  disabled: {(pair.Value ? "true" : "false")}");
                }

                lines.Add(ManagedEnd);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, string.Join(lineEnding, lines) + lineEnding);
        }

        /// <summary>按行拆分(先把 CRLF 归一为 LF,写出时再按原风格拼接)。</summary>
        private static List<string> SplitLines(string text)
            => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        /// <summary>定位托管区块的行范围(含两行标记);没有则返回 (-1, -1)。</summary>
        private static (int Begin, int End) FindManagedRange(List<string> lines)
        {
            var begin = lines.FindIndex(line => line.Contains(ManagedBegin, StringComparison.Ordinal));
            if (begin < 0)
            {
                return (-1, -1);
            }

            var end = lines.FindIndex(begin, line => line.Contains(ManagedEnd, StringComparison.Ordinal));
            return (begin, end < 0 ? lines.Count - 1 : end);
        }

        /// <summary>
        /// 去掉 YAML 标量的包裹引号(单引号内的 <c>''</c> 是转义的单引号)。
        /// 注意:<c>PluginService</c> 解析 dump-config 时用的是同一条规则(那里还要处理双引号包法)。
        /// </summary>
        private static string Unquote(string value)
            => value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
                ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
                : value.Trim('"');
    }
}
