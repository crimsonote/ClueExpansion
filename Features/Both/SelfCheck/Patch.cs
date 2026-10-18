using ClueDetail.Core;
using HarmonyLib;

namespace ClueDetail.Features.Both.SelfCheck
{
    internal partial class SelfCheckFeature
    {
        /// <summary>数据表就绪后输出一次诊断（<see cref="Diagnostics.ReportOnce"/> 自己保证只输出一次）。</summary>
        [HarmonyPatch(typeof(Managers), "Update")]
        internal static class UpdateHook
        {
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<SelfCheckFeature>()) return;
                Diagnostics.ReportOnce();
            }
        }
    }
}
