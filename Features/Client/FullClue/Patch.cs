using ClueDetail.Core;
using HarmonyLib;

namespace ClueDetail.Features.Client.FullClue
{
    internal partial class FullClueFeature
    {
        /// <summary>
        /// 每帧一次状态检查（很轻：一个 State 比较 + 一个布尔判断），
        /// 由 <see cref="FullClueScan"/> 自己决定要不要发包、发几个。
        /// </summary>
        [HarmonyPatch(typeof(Managers), "Update")]
        internal static class UpdateHook
        {
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<FullClueFeature>()) return;
                FullClueScan.Tick();
            }
        }
    }
}
