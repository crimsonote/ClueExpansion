using ClueDetail.Core;
using HarmonyLib;

namespace ClueDetail.Features.UI.LobbyToggle
{
    internal partial class LobbyToggleFeature
    {
        /// <summary>
        /// 建行。弹窗实例被缓存，<c>Init</c> 只成功一次（<c>InitBase._init</c> 守卫），
        /// 所以 <c>__result == false</c> 时说明此前已建过，不要重复建。
        /// </summary>
        [HarmonyPatch(typeof(UI_LobbyPreset), nameof(UI_LobbyPreset.Init))]
        internal static class InitHook
        {
            private static void Postfix(UI_LobbyPreset __instance, bool __result)
            {
                if (!FeatureLoader.Enabled<LobbyToggleFeature>()) return;
                if (__result)
                {
                    LobbyToggle.Ensure(__instance);
                }
            }
        }

        /// <summary>每次打开弹窗都走这里：同步配置值 + 房主闸门。</summary>
        [HarmonyPatch(typeof(UI_LobbyPreset), nameof(UI_LobbyPreset.Refresh))]
        internal static class RefreshHook
        {
            private static void Postfix(UI_LobbyPreset __instance)
            {
                if (!FeatureLoader.Enabled<LobbyToggleFeature>()) return;
                LobbyToggle.Sync(__instance);
            }
        }
    }
}
