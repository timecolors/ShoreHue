namespace ShoreHue.Core.Services.Configuration
{
    /// <summary>
    /// 插件信任库（★ **宿主专用，绝不递给外来代码**）。
    ///
    /// 为什么单独拆一个 **internal** 接口：`ISettingsService` 会经 `HostCapabilities.Settings`
    /// 交给**外来插件**；一旦信任的**写**留在那个接口上，插件就能给自己（乃至任意包）登记信任 ——
    /// 下次加载时哈希命中 → 直接跳过沙箱，而且记录**落盘**（持久化提权，重启后依然免检）。
    /// 这条路径实测可达：插件能自行算出信任哈希（`WidgetCompiler.ComputeHash` 用的
    /// SHA256/Convert/Encoding 都不在沙箱黑名单里）。
    ///
    /// 因此：信任的读与写都只由宿主通过本接口进行；对外暴露的 `ISettingsService` 里**没有**这些成员。
    /// 详见 docs/SECURITY.md（信任层与已知边界）。
    /// </summary>
    internal interface IPluginTrustStore
    {
        /// <summary>该插件当前内容是否被用户显式信任（内容变化 → 哈希不匹配 → 自动失效）。</summary>
        bool IsPluginTrusted(string pluginId, string contentHash);

        /// <summary>记录信任（界面点"信任"、或海床保存可信项时调用）。</summary>
        void SetPluginTrusted(string pluginId, string contentHash);

        /// <summary>撤销信任。</summary>
        void RevokePluginTrust(string pluginId);
    }
}
