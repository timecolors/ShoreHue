using System;

namespace ShoreHue.Core.Controllers
{
    public interface IPanelVisibilityController
    {
        bool IsLocked { get; }
        bool IsVisible { get; }
        double Opacity { get; set; }

        /// <summary>
        /// ★★★ 是否正在延时隐藏计时中 ★★★
        /// </summary>
        bool IsInHideDelay { get; }

        void SetPanelLock(bool locked);

        /// <summary>浮层（任务栏分组弹层）展开期间临时保持显示，避免鼠标移上去面板自动收起。</summary>
        void SetTransientKeepVisible(bool keep);
        void Show(string edge = "");
        void Show();
        void Hide();
        void ForceHide();
        void CancelHide();
        void HideWithDelay();
        bool IsMouseNearPanel();
        void UpdateEdge(string edge);

        event Action? PanelHidden;
        event Action? PanelShown;
    }
}