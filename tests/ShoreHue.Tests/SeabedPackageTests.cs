using ShoreHue.Core.Models;
using ShoreHue.UI.Seabed;
using System;
using System.IO;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>验证 .shpkg 预设包导出/导入往返与权限检测（并覆盖旧版 .dbp 兼容读取）。</summary>
public class SeabedPackageTests
{
    [Fact]
    public void ExportImport_CustomItem_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), "pkg_" + Guid.NewGuid().ToString("N") + ".shpkg");
        try
        {
            var cp = new CustomPanelDefinition
            {
                Id = "custom_test",
                Name = "测试面板",
                Kind = "Panel",
                BaseType = "Panel",
                ParentKey = "panel-features",
                SourceKey = "panel-notification",
                ConfigJson = "{}",
                Source = "using System.Windows;\npublic class X { void M() { System.Net.Http.HttpClient c = null; } }"
            };

            string? err = SeabedPackage.ExportCustom(cp, path);
            Assert.Null(err);

            var result = SeabedPackage.Import(path, out string? importErr);
            Assert.Null(importErr);
            Assert.NotNull(result);
            Assert.Equal("测试面板", result!.Name);
            Assert.Equal("Panel", result.Kind);
            Assert.Equal("panel-features", result.ParentKey);
            Assert.Equal("panel-notification", result.SourceKey);
            Assert.Contains("network", result.Permissions);   // 导入时刻重新检测
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void ExportImport_FullPreset_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), "pkg_" + Guid.NewGuid().ToString("N") + ".shpkg");
        try
        {
            var data = new ShoreHue.Core.Services.Configuration.SettingsData
            {
                Edge_Left = false,
                TriggerDistancePx = 9,
                ShowAnimationType = "Zoom"
            };
            string? err = SeabedPackage.ExportFullPreset("我的整套", data, path);
            Assert.Null(err);

            var result = SeabedPackage.Import(path, out string? importErr);
            Assert.Null(importErr);
            Assert.NotNull(result);
            Assert.Equal("Full", result!.Kind);
            Assert.NotNull(result.FullData);
            Assert.False(result.FullData!.Edge_Left);
            Assert.Equal(9, result.FullData.TriggerDistancePx);
            Assert.Equal("Zoom", result.FullData.ShowAnimationType);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void LegacyDbpExtension_StillImports()
    {
        // ★ 兼容：旧版 .dbp（DynamicBird 时代的缩写）包仍能被读取 —— 改名不影响老文件
        string path = Path.Combine(Path.GetTempPath(), "legacy_" + Guid.NewGuid().ToString("N") + ".dbp");
        try
        {
            var cp = new CustomPanelDefinition
            {
                Id = "custom_legacy",
                Name = "旧扩展名包",
                Kind = "Widget",
                BaseType = "Widget",
                ParentKey = "panel-widgets",
                Source = "using System.Windows.Controls; public class W : UserControl { }"
            };
            Assert.Null(SeabedPackage.ExportCustom(cp, path));
            Assert.True(File.Exists(path));

            var imported = SeabedPackage.Import(path, out string? err);
            Assert.NotNull(imported);
            Assert.Equal("旧扩展名包", imported!.Name);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Import_CorruptFile_ReturnsError()
    {
        string path = Path.Combine(Path.GetTempPath(), "bad_" + Guid.NewGuid().ToString("N") + ".shpkg");
        try
        {
            File.WriteAllText(path, "not a zip");
            var result = SeabedPackage.Import(path, out string? err);
            Assert.Null(result);
            Assert.False(string.IsNullOrEmpty(err));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
