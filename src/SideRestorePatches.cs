using EFT;
using HarmonyLib;

namespace NoPmcTruce;

/// <summary>
/// 补丁点 1（4.1.x 官方位置）：<c>BotCreationData.ChooseProfile(List&lt;Profile&gt;, bool)</c> 的 postfix。
/// </summary>
[HarmonyPatch(typeof(BotCreationData), nameof(BotCreationData.ChooseProfile))]
internal static class ChooseProfileSideRestorePatch
{
    [HarmonyPostfix]
    private static void Postfix(Profile __result)
    {
        if (__result is null)
        {
            return;
        }

        Plugin.SeenFromChooseProfile++;
        Plugin.AnnounceOnce("BotCreationData.ChooseProfile");

        if (SideRestore.TryRestore(__result, "ChooseProfile"))
        {
            Plugin.RestoredFromChooseProfile++;
        }
    }
}

/// <summary>
/// 补丁点 2（兜底）：<c>BotOwner.Create(Player, GameObject, GameDateTime, BotsController, bool, AICorePoint)</c>
/// 的 postfix。这是每个 bot 都会经过的静态入口，参数直接带 <c>Player</c>，
/// 因此无论上游用的是 <c>ChooseProfile</c> 还是预先拼好的 profiles 列表，这里都能兜住。
/// </summary>
[HarmonyPatch(typeof(BotOwner), nameof(BotOwner.Create))]
internal static class BotOwnerCreateSideRestorePatch
{
    [HarmonyPostfix]
    private static void Postfix(Player player)
    {
        if (player is null)
        {
            return;
        }

        Plugin.SeenFromBotOwnerCreate++;
        Plugin.AnnounceOnce("BotOwner.Create");

        var profile = player.Profile;
        if (profile is null)
        {
            return;
        }

        if (SideRestore.TryRestore(profile, "BotOwner.Create"))
        {
            Plugin.RestoredFromBotOwnerCreate++;
        }
    }
}
