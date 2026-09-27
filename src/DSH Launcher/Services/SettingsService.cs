using System;
using System.IO;
using System.Text.Json;
using DSH_Launcher.Models;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 设置服务(单例)。文件保存在:
    /// Windows:%APPDATA%\DSH Launcher\Settings\settings.json;
    /// macOS:~/Library/Application Support/DSH Launcher/Settings/settings.json。
    /// 本机防御(仅 Windows 需要回退链)已由 <see cref="PlatformProcess.RoamingAppDataDirectory"/> 收敛。
    /// </summary>
    public sealed class SettingsService
    {
        public static SettingsService Instance { get; } = new();

        /// <summary>
        /// 设置文件路径。必须是延迟求值:应用启动极早期解析用户目录可能瞬时为空
        /// (Windows 上 SHGetKnownFolderPath 未就绪),静态字段会把空值永久固化;
        /// 每次使用时现算即可拿到正确路径。
        /// </summary>
        private static string SettingsFilePath => Path.Combine(
            PlatformProcess.RoamingAppDataDirectory, "DSH Launcher", "Settings", "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public AppSettings Settings { get; private set; } = new();

        /// <summary>设置被修改并保存后触发。</summary>
        public event Action? SettingsChanged;

        /// <summary>
        /// 加载阶段产生的一条诊断(成功时是"从哪加载、关键项取值",失败时是原因),保留到被取走一次。
        /// <para>
        /// 为什么不在这里直接写日志面板:设置层是更底层的一层,不该反过来依赖 <see cref="DshService"/>
        /// (那会形成 SettingsService ↔ DshService 的环,而且发生在静态初始化期间)。
        /// 由 App 在启动流程里取走并转写:环没了,用户仍能在面板里看到同一句话。
        /// </para>
        /// </summary>
        private string? _loadDiagnostic;

        private SettingsService()
        {
            Load();
        }

        /// <summary>取走加载诊断(取过一次即为 null,避免重复写日志)。</summary>
        public string? TakeLoadDiagnostic()
        {
            var diagnostic = this._loadDiagnostic;
            this._loadDiagnostic = null;
            return diagnostic;
        }

        /// <summary>从磁盘加载设置;文件不存在或损坏时保持默认值。</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    this._loadDiagnostic = $"[设置] 已从 {SettingsFilePath} 加载: 单击={Settings.TraySingleClick} 双击={Settings.TrayDoubleClick}";
                }
            }
            catch (Exception ex)
            {
                this._loadDiagnostic = $"[设置] 加载失败(回退默认值): {ex.GetType().Name}: {ex.Message}";
                // 文件损坏时回退到默认设置
                Settings = new AppSettings();
            }
        }

        /// <summary>更新设置并立即持久化。</summary>
        public void Update(Action<AppSettings> update)
        {
            update(Settings);
            Save();
            SettingsChanged?.Invoke();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
                File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Settings, JsonOptions));
            }
            catch (Exception)
            {
                // 保存失败不影响运行
            }
        }
    }
}
