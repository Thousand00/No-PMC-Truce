using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NoPmcTruce;

/// <summary>
/// No PMC Truce —— 修复 SPT 5.0 里 USEC 与 BEAR 的 PMC 不互相攻击。
///
/// 根因：服务端 Controllers/BotController.cs:290-294 在下发前把每个 PMC 的 Info.Side
/// 强制压成 Savage（注释：Client expects Side for PMCs to be Savage）。4.1.x 靠客户端补丁
/// PmcBotSidePatch 在 BotCreationData.ChooseProfile 的 postfix 里还原，5.0 发行版没有这个补丁，
/// 于是客户端把所有 PMC 当成 Savage 单位，敌意判定落到 SavagePlayerBehaviour=Warn 而非敌对，
/// 服务端地图数据里那份 100% 敌对 / 最高 75% 概率的表被完全旁路。
///
/// 本插件做三件事，都只改客户端内存，不碰任何配置文件：
///   1) 还原 Side：把 pmcBEAR/pmcUSEC 的 Info.Side 改回 Bear/Usec（前提条件）；
///   2) 跨营敌意：在 BotGroupHostility 构造前修正 AHS 表里「USEC ↔ BEAR」的行为/概率；
///   3) 同营敌意：让不同小队的同阵营 PMC 之间也可能开战（原版同营永不互打）。
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.dsh.nopmctruce";
    public const string PluginName = "No PMC Truce";
    public const string PluginVersion = "2.1.0";

    internal static ManualLogSource Logger = null!;

    // ---------- 开关 ----------

    internal static ConfigEntry<bool> EnableSideRestore = null!;
    internal static ConfigEntry<bool> EnableHostilityTuning = null!;

    // ---------- 跨营敌意 ----------

    internal static ConfigEntry<string> CrossFactionBehaviour = null!;
    internal static ConfigEntry<int> CrossFactionChance = null!;

    // ---------- 同营敌意 ----------

    internal static ConfigEntry<bool> EnableSameFaction = null!;
    internal static ConfigEntry<int> SameFactionChance = null!;

    // ---------- 关系修正 ----------

    internal static ConfigEntry<bool> RemovePmcFromNeutral = null!;
    internal static ConfigEntry<bool> RemovePmcFromAlwaysFriends = null!;
    internal static ConfigEntry<bool> KeepZryachiyFriendly = null!;

    // ---------- 诊断 ----------

    internal static ConfigEntry<bool> VerboseLog = null!;

    /// <summary>每个补丁点首次命中时无条件打一行，用于确认补丁是否真的被调用。</summary>
    private static readonly HashSet<string> AnnouncedSites = new();

    internal static int RestoredFromChooseProfile;
    internal static int RestoredFromBotOwnerCreate;
    internal static int SeenFromChooseProfile;
    internal static int SeenFromBotOwnerCreate;

    public override void Load()
    {
        Logger = Log;

        EnableSideRestore = Config.Bind(
            "1. 核心修复",
            "EnableSideRestore",
            true,
            "把 PMC 的 Info.Side 从 Savage 还原成 Bear / Usec。\n"
            + "这是整个修复的前提，关闭后本插件不会有任何效果。");

        EnableHostilityTuning = Config.Bind(
            "1. 核心修复",
            "EnableHostilityTuning",
            true,
            "允许「跨营敌意」「同营敌意」「关系修正」各项生效。\n"
            + "关闭后只用服务端下发的原始敌意表。");

        CrossFactionBehaviour = Config.Bind(
            "2. 跨营敌意（USEC ↔ BEAR）",
            "Behaviour",
            "AlwaysEnemies",
            new ConfigDescription(
                "USEC 与 BEAR 之间的敌对方式。\n"
                + "RespectBackend = 完全按服务端原值，不做任何覆盖；\n"
                + "Chanced = 按下面的 Chance 掷骰；\n"
                + "AlwaysEnemies = 无条件敌对，见面就打（最激烈）。",
                new AcceptableValueList<string>("RespectBackend", "Chanced", "AlwaysEnemies")));

        CrossFactionChance = Config.Bind(
            "2. 跨营敌意（USEC ↔ BEAR）",
            "Chance",
            -1,
            new ConfigDescription(
                "当 Behaviour = Chanced 时的跨营敌对概率（百分比）。\n"
                + "-1 = 不干预，沿用服务端原值（海关/森林 25、灯塔 35、立交桥/街区 15、迷宫 75）；\n"
                + "0 = 永不互相攻击；100 = 必然互相攻击。\n"
                + "Behaviour 为 AlwaysEnemies 或 RespectBackend 时此项不起作用。",
                new AcceptableValueRange<int>(-1, 100)));

        EnableSameFaction = Config.Bind(
            "3. 同营敌意（USEC ↔ USEC / BEAR ↔ BEAR）",
            "EnableSameFaction",
            true,
            "让同阵营、但属于不同小队的 PMC 之间也可能互相攻击。\n"
            + "原版设计里同阵营永远是友军（USEC 见 USEC 不打），开启后改为按下面的概率判定。\n"
            + "同一小队内部的成员不会被这条影响。");

        SameFactionChance = Config.Bind(
            "3. 同营敌意（USEC ↔ USEC / BEAR ↔ BEAR）",
            "SameFactionChance",
            100,
            new ConfigDescription(
                "同阵营之间的互相敌对概率（百分比），与跨营概率完全独立。\n"
                + "-1 = 不干预（保持原版同营永不互打）；\n"
                + "0 = 永不攻击；100 = 必然互相攻击。\n"
                + "想保留原版手感就设 -1 或关掉上面的开关。",
                new AcceptableValueRange<int>(-1, 100)));

        RemovePmcFromNeutral = Config.Bind(
            "4. 关系修正",
            "RemovePmcFromNeutral",
            true,
            "把对手 PMC 从 Neutral（中立）列表里移除。\n"
            + "中立优先级高于概率，留在 Neutral 里会导致永远不打。");

        RemovePmcFromAlwaysFriends = Config.Bind(
            "4. 关系修正",
            "RemovePmcFromAlwaysFriends",
            true,
            "把对手 PMC 从 AlwaysFriends（永远友好）列表里移除。\n"
            + "友好优先级最高，留着就绝无可能交火。");

        KeepZryachiyFriendly = Config.Bind(
            "4. 关系修正",
            "KeepZryachiyFriendly",
            true,
            "保持 PMC 与 Zryachiy 系（bossZryachiy / followerZryachiy / 相关事件角色）的友好关系。\n"
            + "关闭后这些角色也会被从友好列表里清掉，PMC 会和灯塔守卫打起来。");

        VerboseLog = Config.Bind(
            "5. 诊断",
            "Verbose",
            false,
            "每个补丁点首次命中都打一行，且每次还原 Side / 修正敌意表都打日志（排查用，日常可关闭）。");

        try
        {
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo(
                $"{PluginName} {PluginVersion} 已加载（Side 还原={EnableSideRestore.Value}，"
                + $"敌意调整={EnableHostilityTuning.Value}，跨营={CrossFactionBehaviour.Value}/{CrossFactionChance.Value}，"
                + $"同营={EnableSameFaction.Value}/{SameFactionChance.Value}）。");
        }
        catch (Exception ex)
        {
            // 绝不把异常抛给 BepInEx 加载链：补丁失败只应影响本功能，不该让游戏起不来。
            Logger.LogError($"{PluginName} 补丁应用失败，功能未生效（游戏其余部分不受影响）：{ex}");
        }
    }

    /// <summary>补丁点首次命中时无条件记录一行。</summary>
    internal static void AnnounceOnce(string site)
    {
        lock (AnnouncedSites)
        {
            if (!AnnouncedSites.Add(site))
            {
                return;
            }
        }

        Logger.LogInfo($"[site] {site} 首次命中，补丁确实被调用。");
    }
}
