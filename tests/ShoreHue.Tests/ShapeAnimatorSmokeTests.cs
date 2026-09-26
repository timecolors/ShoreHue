using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ShoreHue.Animation;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using Xunit;

namespace ShoreHue.Tests
{
    /// <summary>
    /// ShapeAnimator 烟雾测试：重构（WPF 动画/渲染帧跟随/切换）后所有公开 API 调用不得抛异常。
    /// 防止"面板移动 bug"回归（跟随/切换/滑入滑出/飞行之间的状态互斥）。
    /// </summary>
    [Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须串行（见 WidgetStoreCollection 注释）
    public class ShapeAnimatorSmokeTests : IDisposable
    {
        private readonly string _dir;

        /// <summary>★ 隔离数据目录：本测试只要构造 SettingsManager，不隔离就会读写**用户真实的 config.json**
        /// （既是"测试污染真实配置"，也让结果依赖用户当前设置 → 换台机器就飘）。</summary>
        public ShapeAnimatorSmokeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sh_anim_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            AppPaths.TestDataRoot = _dir;
        }

        public void Dispose()
        {
            AppPaths.TestDataRoot = null;
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void AllPublicMethods_NoThrow()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var settings = new SettingsManager();
                    var w = new Window
                    {
                        Width = 100,
                        Height = 50,
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false,
                        Left = 0,
                        Top = 0
                    };
                    var panel = new Border();
                    w.Content = panel;

                    var anim = new ShapeAnimator(w, panel);
                    anim.SetSettings(settings);
                    anim.SetAnimationsEnabled(true);

                    // ★ 节能降帧：PowerSaver 跳帧（含钳制边界）
                    anim.SetFrameSkip(2);        // 省电 ~20fps
                    anim.SetFrameSkip(0);        // 恢复正常满帧
                    anim.SetFrameSkip(-5);       // 钳制到 0
                    anim.SetFrameSkip(99);       // 钳制到 3

                    anim.SetPositionAndSizeTarget(0, 0, 200, 100);   // 切换
                    anim.SetSizeTarget(150, 80);                     // 尺寸
                    anim.SetPositionTargetWithoutReset(10, 10);      // 跟随(动画重定向)
                    anim.StartFollowPosition();                      // 渲染帧跟随
                    anim.StopFollowPosition();
                    anim.SetShowHideTarget(-100, -100, 0, allowOffscreen: true); // 滑出
                    anim.SetShowHideTarget(50, 50, 0.85, allowOffscreen: true);  // 滑入
                    anim.SetOpacityTarget(0.5);
                    anim.SetFlyParameters(300);
                    anim.StartFly(300, 300);                         // 飞行
                    anim.SetSizeDirect(120, 60);
                    anim.JumpTo(0, 0, 100, 50);
                    anim.SetClingParameters();
                    anim.SetClingTarget(200, 200);
                    anim.ExitClingParameters();
                    anim.StopAll();
                    anim.Dispose();
                    w.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "ShapeAnimator 冒烟测试超时");

            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(
                    $"ShapeAnimator 异常: {failure.GetType().Name}: {failure.Message}");
            }
        }
    }
}
