using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NoPmcTruce;

/// <summary>
/// 在 <c>BotGroupHostility</c> 构造前修正服务端下发的敌意表。
///
/// 切入口：<c>BotGroupHostility(BotsGroup, BotOwner, Il2CppReferenceArray&lt;AdditionalHostilitySettings&gt;, BotsController)</c>。
/// 客户端 5.0 每个 BotsGroup 创建时都会 new 一个 BotGroupHostility，并把 backend 下发的整张 AHS
/// 交给它，由它把 AlwaysFriends / Neutral / ChancedEnemies / 各行为字段灌进 BotGroupInnerHostilityData。
/// 在这之前改表，改动会被正常带进内层结构，不需要绕任何业务逻辑。
///
/// 只改 pmcBEAR 与 pmcUSEC 两条目里「指向另一个 PMC 阵营」的部分，其余角色一律不动。
/// </summary>
[HarmonyPatch]
internal static class HostilityTuningPatch
{
    /// <summary>Zryachiy 系角色：默认保持与 PMC 友好（灯塔守卫）。</summary>
    private static readonly WildSpawnType[] ZryachiyRoles =
    {
        WildSpawnType.bossZryachiy,
        WildSpawnType.followerZryachiy,
        WildSpawnType.peacefullZryachiyEvent,
        WildSpawnType.ravangeZryachiyEvent,
    };

    /// <summary>已处理过的 AHS 表实例（按原生指针），避免每个 BotsGroup 都重复遍历同一份数据。</summary>
    private static readonly HashSet<IntPtr> TouchedTables = new();

    private static int _appliedCount;

    private static MethodBase TargetMethod()
    {
        return AccessTools.Constructor(
            typeof(BotGroupHostility),
            new[]
            {
                typeof(BotsGroup),
                typeof(BotOwner),
                typeof(Il2CppReferenceArray<AdditionalHostilitySettings>),
                typeof(BotsController),
            });
    }

    [HarmonyPrefix]
    private static void Prefix(Il2CppReferenceArray<AdditionalHostilitySettings>? backendAdditionalHostilitySettings)
    {
        try
        {
            if (!Plugin.EnableHostilityTuning.Value)
            {
                return;
            }

            Plugin.AnnounceOnce("BotGroupHostility.ctor");

            if (backendAdditionalHostilitySettings is null)
            {
                return;
            }

            var tablePtr = backendAdditionalHostilitySettings.Pointer;
            lock (TouchedTables)
            {
                if (!TouchedTables.Add(tablePtr))
                {
                    return; // 同一份表只改一次。
                }
            }

            var report = Apply(backendAdditionalHostilitySettings);

            if (report.Touched == 0)
            {
                if (Plugin.VerboseLog.Value)
                {
                    Plugin.Logger.LogInfo("[hostility] 表中没有 PMC 条目，本次无需改动。");
                }

                return;
            }

            _appliedCount++;
            Plugin.Logger.LogInfo(
                $"[hostility] 敌意表已修正（第 {_appliedCount} 次应用，PMC 条目 {report.Touched} 条）："
                + $"跨营模式={Plugin.CrossFactionBehaviour.Value}，"
                + $"中性移除 {report.NeutralRemoved}，友好移除 {report.FriendsRemoved}，"
                + $"跨营敌对加入 {report.EnemiesAdded}，跨营概率改写 {report.ChanceChanged}，"
                + $"同营概率改写 {report.SameChanceChanged}。");
        }
        catch (Exception ex)
        {
            // 敌意调整是增强项，失败绝不能影响 bot 正常生成。
            Plugin.Logger.LogError($"[hostility] 修正敌意表时出错（已忽略，表保持原样）：{ex}");
        }
    }

    private static Report Apply(Il2CppReferenceArray<AdditionalHostilitySettings> table)
    {
        var report = new Report();

        var mode = Plugin.CrossFactionBehaviour.Value;
        var chance = Plugin.CrossFactionChance.Value;

        for (var i = 0; i < table.Length; i++)
        {
            AdditionalHostilitySettings? entry;
            try
            {
                entry = table[i];
            }
            catch (Exception)
            {
                continue; // 个别元素为空指针时不中断整表处理。
            }

            if (entry is null)
            {
                continue;
            }

            var ownerRole = entry.BotRole;
            if (ownerRole != WildSpawnType.pmcBEAR && ownerRole != WildSpawnType.pmcUSEC)
            {
                continue; // 只处理 PMC 自己的条目。
            }

            report.Touched++;

            // 对方阵营（该条目应该敌对的那个 PMC 类型）。
            var opponent = ownerRole == WildSpawnType.pmcBEAR
                ? WildSpawnType.pmcUSEC
                : WildSpawnType.pmcBEAR;

            // --- 1) 从 Neutral 里移除对方 ---
            if (Plugin.RemovePmcFromNeutral.Value)
            {
                report.NeutralRemoved += RemoveRole(entry, isNeutral: true, opponent);
            }

            // --- 2) 从 AlwaysFriends 里移除对方（Zryachiy 系按配置保留）---
            if (Plugin.RemovePmcFromAlwaysFriends.Value)
            {
                report.FriendsRemoved += RemoveRole(entry, isNeutral: false, opponent);
            }

            // --- 3) 设置跨营行为 ---
            switch (mode)
            {
                case "AlwaysEnemies":
                    report.EnemiesAdded += EnsureInAlwaysEnemies(entry, opponent);
                    report.ChanceChanged += SetChance(entry, opponent, 100);
                    break;

                case "Chanced":
                    EnsureNotInAlwaysEnemies(entry, opponent);
                    if (chance >= 0)
                    {
                        report.ChanceChanged += SetChance(entry, opponent, chance);
                    }

                    break;

                default:
                    // RespectBackend：只做上面的关系清理，不碰行为与概率。
                    break;
            }

            // --- 4) 同营敌意：让同阵营、不同小队的 PMC 之间也可能开战 ---
            // 原版数据里 pmcBEAR 条目根本没有指向 pmcBEAR 自己的 ChancedEnemies 项，
            // 所以查不到 → 不掷骰 → 永远不打。这里补上「指向自己阵营」的那一条。
            if (Plugin.EnableSameFaction.Value && Plugin.SameFactionChance.Value >= 0)
            {
                report.SameChanceChanged += SetChance(entry, ownerRole, Plugin.SameFactionChance.Value);
            }
        }

        return report;
    }

    // ---------- 具体写操作 ----------

    /// <summary>把某个角色从 Neutral 或 AlwaysFriends 数组里去掉；保留 Zryachiy 系（可配置）。</summary>
    private static int RemoveRole(
        AdditionalHostilitySettings entry,
        bool isNeutral,
        WildSpawnType target)
    {
        var source = isNeutral ? entry.Neutral : entry.AlwaysFriends;
        if (source is null || source.Length == 0)
        {
            return 0;
        }

        var keepZryachiy = Plugin.KeepZryachiyFriendly.Value;
        var kept = new List<WildSpawnType>(source.Length);
        var removed = 0;

        for (var i = 0; i < source.Length; i++)
        {
            var role = source[i];

            if (role == target && !(keepZryachiy && IsZryachiy(role)))
            {
                removed++;
                continue;
            }

            kept.Add(role);
        }

        if (removed == 0)
        {
            return 0;
        }

        if (isNeutral)
        {
            entry.Neutral = Rebuild(source, kept);
        }
        else
        {
            entry.AlwaysFriends = Rebuild(source, kept);
        }

        return removed;
    }

    /// <summary>确保对方在 AlwaysEnemies 里；返回新增数量。</summary>
    private static int EnsureInAlwaysEnemies(
        AdditionalHostilitySettings entry,
        WildSpawnType opponent)
    {
        var src = entry.AlwaysEnemies;
        if (src is not null)
        {
            for (var i = 0; i < src.Length; i++)
            {
                if (src[i] == opponent)
                {
                    return 0; // 已在里面。
                }
            }
        }

        var oldLen = src?.Length ?? 0;
        var expanded = new Il2CppStructArray<WildSpawnType>(oldLen + 1);
        for (var i = 0; i < oldLen; i++)
        {
            expanded[i] = src![i];
        }

        expanded[oldLen] = opponent;
        entry.AlwaysEnemies = expanded;
        return 1;
    }

    /// <summary>确保对方不在 AlwaysEnemies 里（Chanced 模式要让它走概率分支）。</summary>
    private static void EnsureNotInAlwaysEnemies(
        AdditionalHostilitySettings entry,
        WildSpawnType opponent)
    {
        var src = entry.AlwaysEnemies;
        if (src is null || src.Length == 0)
        {
            return;
        }

        var kept = new List<WildSpawnType>(src.Length);
        var removed = false;
        for (var i = 0; i < src.Length; i++)
        {
            if (src[i] == opponent)
            {
                removed = true;
                continue;
            }

            kept.Add(src[i]);
        }

        if (!removed)
        {
            return;
        }

        entry.AlwaysEnemies = Rebuild(src, kept);
    }

    /// <summary>修改 ChancedEnemies 里指向对方的概率；缺少该条目时补建一条。返回改写数量。</summary>
    private static int SetChance(
        AdditionalHostilitySettings entry,
        WildSpawnType opponent,
        int wanted)
    {
        var chanced = entry.ChancedEnemies;
        var changed = 0;

        if (chanced is not null)
        {
            for (var j = 0; j < chanced.Length; j++)
            {
                var item = chanced[j];
                if (item is null || item.Role != opponent)
                {
                    continue;
                }

                if (item.EnemyChance == wanted)
                {
                    continue;
                }

                item.EnemyChance = wanted;
                changed++;
            }

            if (changed > 0)
            {
                return changed;
            }

            // 已存在但没有改动需求 → 无需补建。
            for (var j = 0; j < chanced.Length; j++)
            {
                if (chanced[j] is not null && chanced[j].Role == opponent)
                {
                    return 0;
                }
            }
        }

        // 表里没有指向对方的概率条目 → 补一条。
        var newItem = new AdditionalHostilitySettings.ChancedEnemy
        {
            Role = opponent,
            EnemyChance = wanted,
        };

        var oldLen = chanced?.Length ?? 0;
        var expanded = new Il2CppReferenceArray<AdditionalHostilitySettings.ChancedEnemy>(oldLen + 1);
        for (var j = 0; j < oldLen; j++)
        {
            expanded[j] = chanced![j];
        }

        expanded[oldLen] = newItem;
        entry.ChancedEnemies = expanded;
        return 1;
    }

    /// <summary>按保留列表重建同类型数组。</summary>
    private static Il2CppStructArray<WildSpawnType> Rebuild(
        Il2CppStructArray<WildSpawnType> source,
        List<WildSpawnType> kept)
    {
        var rebuilt = new Il2CppStructArray<WildSpawnType>(kept.Count);
        for (var i = 0; i < kept.Count; i++)
        {
            rebuilt[i] = kept[i];
        }

        _ = source;
        return rebuilt;
    }

    private static bool IsZryachiy(WildSpawnType role)
    {
        for (var i = 0; i < ZryachiyRoles.Length; i++)
        {
            if (ZryachiyRoles[i] == role)
            {
                return true;
            }
        }

        return false;
    }

    private struct Report
    {
        internal int Touched;
        internal int NeutralRemoved;
        internal int FriendsRemoved;
        internal int EnemiesAdded;
        internal int ChanceChanged;
        internal int SameChanceChanged;
    }
}
