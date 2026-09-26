using System.Collections.Generic;

namespace ShoreHue.Animation
{
    /// <summary>
    /// 自定义动画注册表：海床「动画」分组的 IAnimation 插件编译后注册到这里，
    /// ShapeAnimator.SetShowHideTarget 按动画类型 Id 查表分发；
    /// 设置页动画类型下拉也从这里取自定义动画列表（Name 展示 / Id 存储）。
    /// 注册表是进程内静态字典，由 WidgetPluginStore.ReloadAnimations 整体重建。
    /// </summary>
    public static class AnimationRegistry
    {
        private static readonly Dictionary<string, IAnimation> _map = new();
        private static readonly object _lock = new object();

        /// <summary>自定义动画抛异常时上报（宿主订阅后交给插件守卫熔断）。★ 放在这里是为了
        /// 让 ShapeAnimator 只依赖动画命名空间，不反向依赖 UI 层。</summary>
        public static event System.Action<System.Exception>? CustomAnimationError;

        public static void ReportCustomAnimationError(System.Exception ex)
        {
            try { CustomAnimationError?.Invoke(ex); } catch { /* 订阅方异常不能反过来打断动画收尾 */ }
        }

        /// <summary>查自定义动画（key = 动画 Id）。未命中返回 false。</summary>
        public static bool TryGet(string id, out IAnimation? animation)
        {
            lock (_lock) return _map.TryGetValue(id ?? "", out animation);
        }

        /// <summary>当前已注册的自定义动画快照（供设置页下拉构建选项）。</summary>
        public static IReadOnlyList<IAnimation> All
        {
            get { lock (_lock) return new List<IAnimation>(_map.Values); }
        }

        /// <summary>整体替换（ReloadAnimations 扫描完调用）。清空后重新注册，保证与文件夹一致。</summary>
        public static void ReplaceAll(IDictionary<string, IAnimation> animations)
        {
            lock (_lock)
            {
                _map.Clear();
                if (animations != null)
                {
                    foreach (var kvp in animations) _map[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>注册单个动画（手动/测试用）。</summary>
        public static void Register(IAnimation animation)
        {
            if (animation == null || string.IsNullOrEmpty(animation.Id)) return;
            lock (_lock) _map[animation.Id] = animation;
        }

        /// <summary>注销单个动画。</summary>
        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock) _map.Remove(id);
        }

        /// <summary>清空注册表。</summary>
        public static void Clear()
        {
            lock (_lock) _map.Clear();
        }
    }
}