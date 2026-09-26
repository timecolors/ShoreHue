// ==================== 市场包的身份规则（纯函数，可单测） ====================
//
// 为什么单独成类：这是**安全闸门**。放在 GitHubMarketService 里就只能靠"人工点一遍"验证，
// 而这里每一条都能写成断言（见 tests\ShoreHue.Tests\MarketPackageRulesTests.cs）。
//
// 背景（2026-09-13 发现的真实漏洞）：上传时"同 id 已存在就取 sha 后写入" = **直接覆盖**，
// 而 publisherId 只在**删除**时校验、上传时完全不校验，且包 ID 是用户手填、默认从**显示名**推导
// （中文显示名经 SanitizeId 会退化成同样的英文串）→ 撞名是常态，后发布者会**静默覆盖**前者的包。
//
// 本类给出的规则（fail-closed：拿不准就拒绝）：
//   · 新包  → ID 必须是「你的 GitHub 登录名/短名」，前缀必须属于自己；
//   · 已有包 → publisherId 必须是自己；**缺失归属信息一律拒绝**（宁可拒绝，也不猜）。

using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShoreHue.UI.Seabed
{
    internal static class MarketPackageRules
    {
        internal const string IdHint = "包 ID 要用「你的 GitHub 登录名/短名」的形式，例如 timecolors/tide-note";

        /// <summary>id 形态校验（与登录名、归属无关的那部分）：最多一个斜杠，两段各自合法。顺带防路径穿越。</summary>
        internal static string? ValidateIdFormat(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "包 ID 不能为空";

            string[] parts = id.Split('/');
            if (parts.Length > 2) return "包 ID 最多只能有一个斜杠（形如 登录名/短名）";
            foreach (string p in parts)
            {
                if (!Regex.IsMatch(p, "^[A-Za-z0-9_-]{2,64}$"))
                    return "包 ID 每段只能包含英文/数字/下划线/连字符，长度 2–64：「" + p + "」";
            }
            return null;
        }

        internal static bool IsNamespaced(string? id) => !string.IsNullOrEmpty(id) && id.Contains('/');

        /// <summary>命名空间前缀是否属于该登录名（GitHub 登录名大小写不敏感）。</summary>
        internal static bool NamespaceMatches(string id, string? login)
        {
            int i = id.IndexOf('/');
            return i > 0 && !string.IsNullOrEmpty(login) && string.Equals(id[..i], login, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从既有 manifest 里取归属（容错：缺失/类型不对都当作"未知 = 0"）。</summary>
        internal static (long PublisherId, string Author) ParseOwner(string? manifestJson)
        {
            if (string.IsNullOrWhiteSpace(manifestJson)) return (0, "");
            try
            {
                using var doc = JsonDocument.Parse(manifestJson);
                JsonElement root = doc.RootElement;

                long pid = 0;
                if (root.TryGetProperty("publisherId", out var p))
                {
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long n)) pid = n;
                    else if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out long n2)) pid = n2;
                }

                string author = root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String
                    ? (a.GetString() ?? "")
                    : "";
                return (pid, author);
            }
            catch
            {
                // manifest 坏了 → 视为"归属未知"，由调用方按 fail-closed 处理（拒绝覆盖）
                return (0, "");
            }
        }

        /// <summary>
        /// 能不能发布这个 id：null = 可以，否则是给用户看的错误信息。
        /// ★ 这是「任何人都能覆盖他人包」那个漏洞的闸门，保持纯函数以便单测。
        /// </summary>
        internal static string? CheckCanPublish(string id, string? existingManifestJson, long myPublisherId, string? myLogin)
        {
            if (ValidateIdFormat(id) is string fmt) return fmt;

            bool exists = !string.IsNullOrWhiteSpace(existingManifestJson);

            if (!exists)
            {
                // 新包：必须占自己的命名空间，不能碰裸 ID（官方保留）或别人的命名空间
                if (!IsNamespaced(id))
                    return "新包的 ID 必须是「你的 GitHub 登录名/短名」形式（无斜杠的 ID 是官方保留命名空间）。" + IdHint;
                if (!NamespaceMatches(id, myLogin))
                    return "包 ID 必须以你自己的 GitHub 登录名开头 —— 不能占用别人的命名空间。" + IdHint;
                return null;
            }

            // 已有包：publisherId 必须是自己。缺失归属信息**一律拒绝**（不猜 author 字符串，那可以伪造）
            var (ownerId, owner) = ParseOwner(existingManifestJson);
            if (ownerId <= 0)
                return "这个包没有归属信息（publisherId 缺失），为免误伤他人的包已拒绝覆盖。"
                       + "若这是官方包，请改用仓库 PR 更新。";

            if (ownerId != myPublisherId)
                return "这个包 ID 已被 @" + (string.IsNullOrEmpty(owner) ? "其他作者" : owner) + " 占用，只有原作者能更新。";

            return null;
        }
    }
}
