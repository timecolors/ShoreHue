using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ShoreHue.UI.Panels
{
    /// <summary>组成员：一个应用（exe 路径）或另一个分组（嵌套，照潮池规则）。</summary>
    public sealed class TaskbarGroupMember
    {
        public string Kind { get; set; } = "app";   // app | group
        public string Key { get; set; } = "";
        public TaskbarGroupMember() { }
        public TaskbarGroupMember(string kind, string key) { Kind = kind; Key = key; }
    }

    public sealed class TaskbarGroupDef
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<TaskbarGroupMember> Members { get; set; } = new();
    }

    /// <summary>
    /// 任务栏「窗口标签分组」的纯逻辑 + 持久化模型（存进用户设置，见 SettingsData.TaskbarGroupsJson）。
    ///
    /// 设计（对齐潮池）：分组是"能装标签的标签"。成员按**应用（exe 路径）**记 — 窗口句柄跨重启会变，
    /// 应用路径才是稳定的；窗口来去只影响运行时可见性，分组定义本身持久化。
    /// 嵌套规则与潮池一致：第 1 层正常，第 2 层允许（打开时给彩蛋），第 3 层禁止。
    /// </summary>
    public static class TaskbarGroups
    {
        /// <summary>最大嵌套深度（潮池规则：第 2 层允许，第 3 层禁）。</summary>
        public const int MaxDepth = 2;

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        public static List<TaskbarGroupDef> Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TaskbarGroupDef>();
            try { return JsonSerializer.Deserialize<List<TaskbarGroupDef>>(json!) ?? new List<TaskbarGroupDef>(); }
            catch { return new List<TaskbarGroupDef>(); }   // 坏 JSON 按"没有分组"处理，不炸面板
        }

        public static string Serialize(IEnumerable<TaskbarGroupDef> defs)
            => JsonSerializer.Serialize((defs ?? Enumerable.Empty<TaskbarGroupDef>()).ToList(), JsonOpts);

        public static TaskbarGroupDef? Find(List<TaskbarGroupDef> defs, string id)
            => defs.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));

        public static bool ContainsMember(List<TaskbarGroupDef> defs, string groupId, string kind, string key)
            => Find(defs, groupId)?.Members.Any(m => m.Kind == kind && string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) == true;

        /// <summary>组的嵌套深度：只含应用 = 1；含一层组 = 2；第 3 层禁止。</summary>
        public static int Depth(List<TaskbarGroupDef> defs, string groupId, HashSet<string>? visiting = null)
        {
            visiting ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visiting.Add(groupId)) return MaxDepth + 1;   // 出现环 → 按超深处理（fail-closed）
            var g = Find(defs, groupId);
            if (g == null) return 0;
            int max = 1;
            foreach (var m in g.Members.Where(m => m.Kind == "group"))
            {
                var next = new HashSet<string>(visiting, StringComparer.Ordinal);
                max = Math.Max(max, 1 + Depth(defs, m.Key, next));
            }
            return max;
        }

        /// <summary>能否把内层组再套一层。</summary>
        public static bool CanNest(List<TaskbarGroupDef> defs, string innerGroupId)
            => 1 + Depth(defs, innerGroupId) <= MaxDepth;

        /// <summary>把一个成员加进组；返回 null=成功，否则是给用户看的原因。</summary>
        public static string? AddMember(List<TaskbarGroupDef> defs, string groupId, string kind, string key)
        {
            var g = Find(defs, groupId);
            if (g == null) return "目标分组不存在";
            if (kind == "group")
            {
                if (string.Equals(groupId, key, StringComparison.Ordinal)) return "不能把分组放进它自己";
                if (Find(defs, key) == null) return "要放进来的分组不存在";
                if (!CanNest(defs, key)) return "分组最多套两层（再套一层就找不到东西了）";
            }
            if (g.Members.Any(m => m.Kind == kind && string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
                return null;   // 已在组里：幂等成功
            g.Members.Add(new TaskbarGroupMember(kind, key));
            return null;
        }

        /// <summary>把两个标签并成一组。返回 null=成功（groupId 给出结果组），否则是原因。</summary>
        public static string? Group(List<TaskbarGroupDef> defs, string aKind, string aKey, string bKind, string bKey, out string groupId)
        {
            groupId = "";
            if (aKind == "group" && bKind == "group")
            {
                if (string.Equals(aKey, bKey, StringComparison.Ordinal)) return "两个是同一个分组";
                var err = AddMember(defs, aKey, "group", bKey);
                if (err != null) return err;
                groupId = aKey;
                return null;
            }
            if (aKind == "group") { groupId = aKey; return AddMember(defs, aKey, bKind, bKey); }
            if (bKind == "group") { groupId = bKey; return AddMember(defs, bKey, aKind, aKey); }

            var g = new TaskbarGroupDef();
            g.Members.Add(new TaskbarGroupMember("app", aKey));
            g.Members.Add(new TaskbarGroupMember("app", bKey));
            defs.Add(g);
            groupId = g.Id;
            return null;
        }

        /// <summary>解散一个分组：删掉它，并把别处对它的引用一并去掉。</summary>
        public static void Ungroup(List<TaskbarGroupDef> defs, string groupId)
        {
            defs.RemoveAll(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
            foreach (var g in defs)
                g.Members.RemoveAll(m => m.Kind == "group" && string.Equals(m.Key, groupId, StringComparison.Ordinal));
            PruneEmpty(defs);
        }

        public static void RemoveMember(List<TaskbarGroupDef> defs, string groupId, string kind, string key)
        {
            Find(defs, groupId)?.Members.RemoveAll(m => m.Kind == kind && string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            PruneEmpty(defs);
        }

        /// <summary>成员不足 2 个的组自动解散（含级联：外层组也会随之掉成员）。</summary>
        public static void PruneEmpty(List<TaskbarGroupDef> defs)
        {
            bool changed;
            do
            {
                changed = false;
                var dead = defs.Where(g => g.Members.Count < 2).Select(g => g.Id).ToList();
                if (dead.Count == 0) break;
                defs.RemoveAll(g => dead.Contains(g.Id));
                foreach (var g in defs) g.Members.RemoveAll(m => m.Kind == "group" && dead.Contains(m.Key));
                changed = true;
            } while (changed);
        }
    }
}