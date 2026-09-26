using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.Infrastructure.Utils;
using Xunit;
using System;
using System.IO;

namespace ShoreHue.Tests
{
    /// <summary>
    /// ★ 串行集合：**所有会改动进程级共享状态的测试都必须进这里**（`AppPaths.TestDataRoot` / 小组件仓库静态缓存）。
    /// 原来只有小组件那几个测试进了集合，而 SettingsApplyTests / PluginTrustTests / RegionAnimationTests
    /// 也各自 `TestDataRoot = 临时目录` → 并行跑时你把我置 null、我把你置 null，
    /// 会造成**偶发假红**（同一次提交时绿时红）。加测试时若碰这两个共享状态，请挂上 `[Collection("WidgetStore")]`。
    /// </summary>
    [CollectionDefinition("WidgetStore", DisableParallelization = true)]
    public class WidgetStoreCollection { }

    /// <summary>小组件文件夹化：分组子目录扫描 + .cs/.shpkg(旧 .dbp) 归一化（用户直接在系统文件夹管理）。</summary>
    [Collection("WidgetStore")]
    public class WidgetPluginStoreFolderTests : IDisposable
    {
        private readonly string _dir;

        /// <summary>
        /// ★ 必须隔离数据根：本类会走 `Save` / `Delete`，而 `WidgetPluginStore.RootDir`
        /// 解析自 `AppPaths.DataRoot`。不隔离的话它会在**用户真实的** `%LOCALAPPDATA%\ShoreHue\seabed`
        /// 里建目录、写 main.cs/manifest.json，再 `Directory.Delete(dir, true)` 永久删除；
        /// `Reload` → `EnsureSkeleton` 还会顺带触发真实数据的 `MigrateLegacyWidgetsDir` /
        /// `MigrateGroupLayout` 搬家。用户真有一个叫 `test-widget` 的小组件就会被覆盖后删除。
        /// </summary>
        public WidgetPluginStoreFolderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sh_folder_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            AppPaths.TestDataRoot = _dir;
        }

        public void Dispose()
        {
            AppPaths.TestDataRoot = null;
            try { Directory.Delete(_dir, true); } catch { /* 尽力而为：临时目录残留无害 */ }
        }

        [Fact]
        public void Reload_DoesNotCrash_OnMissingFolder()
        {
            // 根目录不存在时 Reload 应安全（首次运行）
            var plugins = WidgetPluginStore.Installed;
            Assert.NotNull(plugins);
        }

        [Fact]
        public void Save_Then_GetById_RoundTrip()
        {
            var p = new WidgetPlugin
            {
                Id = "test-widget",
                Name = "测试小组件",
                Source = "public class W { public void M() {} }",
                Group = "小组件"
            };
            var err = WidgetPluginStore.Save(p);
            Assert.Equal("", err);
            var loaded = WidgetPluginStore.GetById("test-widget");
            Assert.NotNull(loaded);
            Assert.Equal("测试小组件", loaded!.Name);
            // ★ Group 现在是所在的一级目录名：扁平化后面板都在 面板/ 下，所以是「面板」
            //   （原先写入 面板/小组件/，Group 才是「小组件」）。
            Assert.Equal("面板", loaded.Group);
            WidgetPluginStore.Delete("test-widget");
            Assert.Null(WidgetPluginStore.GetById("test-widget"));
        }

        [Fact]
        public void 保存与删除_只落在隔离的数据根内()
        {
            var p = new WidgetPlugin
            {
                Id = "isolated-probe",
                Name = "隔离探针",
                Source = "public class W { public void M() {} }",
                Group = "小组件"
            };
            Assert.Equal("", WidgetPluginStore.Save(p));
            Assert.StartsWith(_dir, WidgetPluginStore.RootDir, StringComparison.OrdinalIgnoreCase);
            WidgetPluginStore.Delete("isolated-probe");
        }
    }
}
