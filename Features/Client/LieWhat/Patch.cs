using ClueDetail.Core;
using Data;
using HarmonyLib;

namespace ClueDetail.Features.Client.LieWhat
{
    internal partial class LieWhatFeature
    {
        /// <summary>
        /// 平板初始化后补建下拉。
        /// </summary>
        /// <remarks>
        /// <c>UI_GameTablet.Init</c> 返回 false 表示 <c>InitBase._init</c> 守卫拦下了（此前已初始化），
        /// 那时界面上已经有我们的下拉，不要重复建。
        /// </remarks>
        [HarmonyPatch(typeof(UI_GameTablet), nameof(UI_GameTablet.Init))]
        internal static class InitHook
        {
            private static void Postfix(UI_GameTablet __instance, bool __result)
            {
                if (!FeatureLoader.Enabled<LieWhatFeature>()) return;
                if (__result)
                {
                    LieWhatDropdown.Build(__instance);
                }
            }
        }

        /// <summary>
        /// 玩家点了地图上的区域（= 给伪证选房间）之后，按该房间刷新可选活动。
        /// </summary>
        /// <remarks>
        /// 0.1.18 的这个方法是 private（反编译 :53734），所以用字符串名定位。
        /// 旧版在这里调 <c>RefreshWhat(data.DataId)</c>，口径一致。
        /// </remarks>
        [HarmonyPatch(typeof(UI_GameTablet), "OnClickSectorLieSection")]
        internal static class SectorHook
        {
            private static void Postfix(UI_GameTablet __instance, RoomData data)
            {
                if (!FeatureLoader.Enabled<LieWhatFeature>()) return;
                LieWhatDropdown.Refresh(__instance, data);
            }
        }
    }
}