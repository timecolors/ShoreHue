using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;

namespace ShoreHue.UI.Localization
{
    /// <summary>
    /// 本地化管理器（运行时切换语言）：
    ///  - 从嵌入式 resx 读取字符串（中性资源=中文，en-US 卫星资源=英文）
    ///  - 实现 INotifyPropertyChanged，切换语言后所有 Loc 绑定自动刷新
    /// 使用：
    ///   启动时  LocalizationManager.Instance.SetCulture("zh-CN" | "en-US" | ...)
    ///   XAML    Text="{loc:Loc Key=WidgetTabs_Timer}" 或 "{loc:Loc WidgetTabs_Timer}"
    ///   代码    LocalizationManager.Instance["WidgetTabs_Timer"]
    /// </summary>
    public sealed class LocalizationManager : INotifyPropertyChanged
    {
        public static LocalizationManager Instance { get; } = new();

        private readonly ResourceManager _resources;

        /// <summary>进程启动时的系统 UI 语言快照（跟随系统时回退用）。</summary>
        private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

        private LocalizationManager()
        {
            _resources = new ResourceManager(
                "ShoreHue.UI.Localization.Strings", typeof(LocalizationManager).Assembly);
        }

        /// <summary>按 key 取当前语言的字符串；缺失时返回 key 本身（便于发现漏译）。</summary>
        public string this[string key]
        {
            get
            {
                try
                {
                    string? s = _resources.GetString(key, CultureInfo.CurrentUICulture);
                    return string.IsNullOrEmpty(s) ? key : s;
                }
                catch
                {
                    return key;
                }
            }
        }

        /// <summary>当前语言名称（如 zh-CN / en-US）。</summary>
        public string CurrentCultureName => CultureInfo.CurrentUICulture.Name;

        /// <summary>切换语言并通知所有绑定刷新。参数为空时跟随 Windows 系统显示语言。</summary>
        public void SetCulture(string? cultureName)
        {
            try
            {
                var culture = string.IsNullOrWhiteSpace(cultureName)
                    ? GetSystemUiCulture()
                    : new CultureInfo(cultureName);
                CultureInfo.CurrentUICulture = culture;
            }
            catch
            {
                CultureInfo.CurrentUICulture = GetSystemUiCulture();
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        }

        /// <summary>
        /// 跟随 Windows 系统"显示语言"（GetUserDefaultUILanguage：用户实际看到的界面语言，
        /// 而不是安装介质语言 InstalledUICulture——装的中文系统但显示语言改英文时，应显示英文）。
        /// 仅支持中/英；其他语言（日/德…）回退英文。
        /// </summary>
        private static CultureInfo GetSystemUiCulture()
        {
            try
            {
                int lcid = GetUserDefaultUILanguage();
                if (lcid != 0)
                {
                    var c = new CultureInfo(lcid);
                    return Normalize(c.Name);
                }
            }
            catch { /* 这条来源拿不到就试下一条，最后兜底 en-US */ }
            try
            {
                return Normalize(SystemCulture.Name);
            }
            catch { /* 两条来源都拿不到 → 返回 en-US（见下一行） */ }
            return CultureInfo.GetCultureInfo("en-US");
        }

        private static CultureInfo Normalize(string name)
        {
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return CultureInfo.GetCultureInfo("zh-CN");
            if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return CultureInfo.GetCultureInfo("en-US");
            // 其他语言没有本地化资源：回退英文（比中文更通用）
            return CultureInfo.GetCultureInfo("en-US");
        }

        [DllImport("kernel32.dll")]
        private static extern int GetUserDefaultUILanguage();

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
