using System;
using EFT;

namespace NoPmcTruce;

/// <summary>
/// 两个补丁点共用的 Side 还原逻辑。
///
/// 刻意读写的都是 <c>ProfileInfo</c> 的自动属性后备字段 <c>_Side_k__BackingField</c>
/// （public，setter 直接写原生内存偏移），而不用 <c>Info.Side</c>：
/// 5.0 里 <c>Info.Side</c> 的 setter 是 protected，外部无法赋值。
/// </summary>
internal static class SideRestore
{
    /// <summary>最多详细打印多少次，避免刷屏。</summary>
    private const int VerboseLimit = 40;

    private static int _verboseEmitted;

    /// <summary>
    /// 若该 Profile 是 PMC，则把 Side 还原成对应阵营。
    /// 返回 true 表示本次真的写入了（幂等：已是目标值则返回 false）。
    /// </summary>
    internal static bool TryRestore(Profile? profile, string site)
    {
        if (!Plugin.EnableSideRestore.Value)
        {
            return false;
        }

        if (profile is null)
        {
            return false;
        }

        var info = profile.Info;
        if (info is null)
        {
            return false;
        }

        var settings = info.Settings;
        if (settings is null)
        {
            return false;
        }

        var role = settings.Role;

        // 只处理两种 PMC 角色。其它角色一律不碰。
        EPlayerSide wanted;
        if (role == WildSpawnType.pmcBEAR)
        {
            wanted = EPlayerSide.Bear;
        }
        else if (role == WildSpawnType.pmcUSEC)
        {
            wanted = EPlayerSide.Usec;
        }
        else
        {
            return false;
        }

        var before = info._Side_k__BackingField;
        if (before == wanted)
        {
            return false; // 幂等：已经是目标值，不重复写。
        }

        info._Side_k__BackingField = wanted;

        if (Plugin.VerboseLog.Value && _verboseEmitted < VerboseLimit)
        {
            _verboseEmitted++;
            Plugin.Logger.LogInfo(
                $"[restore/{site}] {role}: {before} -> {wanted} (nick={SafeNick(info)}, total={_verboseEmitted})");
        }

        return true;
    }

    private static string SafeNick(ProfileInfo info)
    {
        try
        {
            return info.Nickname ?? "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
