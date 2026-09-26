using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.src.core.Services.Shortcuts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Core.Services
{
    /// <summary>
    /// 快捷方式管理器（实例类，实现 IShortcutService + IService）
    /// </summary>
    public class ShortcutManager : IShortcutService, IService
    {
        private readonly ObservableCollection<ShortcutData> _shortcuts = new();
        private readonly string _dataFilePath;

        public event EventHandler? ShortcutsChanged;

        // ========== IService 实现 ==========
        public string Name => "ShortcutManager";
        public bool IsInitialized { get; private set; } = false;

        public ObservableCollection<ShortcutData> Shortcuts => _shortcuts;

        public ShortcutManager()
        {
            if (!Directory.Exists(AppPaths.DataRoot)) Directory.CreateDirectory(AppPaths.DataRoot);
            _dataFilePath = AppPaths.ShortcutsPath;
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            Load();
            IsInitialized = true;
            LogManager.Debug($"ShortcutManager 初始化完成，已加载 {_shortcuts.Count} 个快捷方式");
        }

        public void Shutdown()
        {
            if (!IsInitialized) return;
            Save();
            IsInitialized = false;
            LogManager.Debug("ShortcutManager 已关闭");
        }

        // ============ 公开方法 ============

        public bool AddShortcut(string path, string? name = null, string? arguments = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if (_shortcuts.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                return false;

            var data = new ShortcutData
            {
                Path = path,
                Name = name ?? Path.GetFileNameWithoutExtension(path),
                Arguments = arguments ?? "",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                Order = _shortcuts.Count > 0 ? _shortcuts.Max(s => s.Order) + 1 : 0
            };

            _shortcuts.Add(data);
            Save();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool RemoveShortcut(string id)
        {
            var target = _shortcuts.FirstOrDefault(s => s.Id == id);
            if (target == null) return false;

            _shortcuts.Remove(target);
            Save();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool RemoveShortcutByPath(string path)
        {
            var target = _shortcuts.FirstOrDefault(s =>
                string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            if (target == null) return false;

            _shortcuts.Remove(target);
            Save();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void MoveShortcut(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= _shortcuts.Count ||
                toIndex < 0 || toIndex >= _shortcuts.Count || fromIndex == toIndex)
                return;

            _shortcuts.Move(fromIndex, toIndex);
            for (int i = 0; i < _shortcuts.Count; i++)
                _shortcuts[i].Order = i;

            Save();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateShortcutName(string id, string newName)
        {
            var target = _shortcuts.FirstOrDefault(s => s.Id == id);
            if (target == null) return;

            target.Name = newName;
            Save();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SaveShortcutsOrder()
        {
            Save();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Reload()
        {
            _shortcuts.Clear();
            Load();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ============ 私有方法 ============

        private void Load()
        {
            if (!File.Exists(_dataFilePath)) return;

            try
            {
                string json = File.ReadAllText(_dataFilePath);
                var list = JsonSerializer.Deserialize<List<ShortcutData>>(json);
                if (list != null)
                {
                    foreach (var item in list.OrderBy(s => s.Order))
                    {
                        _shortcuts.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"加载快捷方式失败", ex);
            }
        }

        private void Save()
        {
            try
            {
                for (int i = 0; i < _shortcuts.Count; i++)
                    _shortcuts[i].Order = i;

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_shortcuts, options);
                File.WriteAllText(_dataFilePath, json);
            }
            catch (Exception ex)
            {
                LogManager.Error($"保存快捷方式失败", ex);
            }
        }

        public ImageSource? GetIcon(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                // ★ using：`Icon` 持有 HICON 句柄，不释放就是每次取图标泄漏一个 GDI 对象。
                //   本方法在任务栏每次显示、快捷方式管理页每次加载时都会调用（每个条目一次）。
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;

                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    new System.Windows.Int32Rect(0, 0, icon.Width, icon.Height),
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch
            {
                return null;
            }
        }
    }
}
