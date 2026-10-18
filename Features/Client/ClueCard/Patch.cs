using ClueDetail.Core;
using HarmonyLib;

namespace ClueDetail.Features.Client.ClueCard
{
    internal partial class ClueCardFeature
    {
        /// <summary>
        /// 原版填完一条线索的内容之后，补上「活动卡面」与「整句文本」两格。
        /// </summary>
        /// <remarks>
        /// <c>UI_ClueSubItem.FillText(ClueManager.Clue)</c> 是 private（0.1.18 :86635），
        /// 所以用字符串名定位。**只用 Postfix**：原版的填空一个字都不改，
        /// 我们只往它没用到的那两格里写东西。
        /// <para>
        /// 节点建立也放在这里而不是 <c>Init</c>：同一个实例会在线索行与伪证行之间复用，
        /// 直到 <c>FillText</c> 才知道这一次是哪一种（两级容器不同）。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(UI_ClueSubItem), "FillText")]
        internal static class FillTextHook
        {
            private static void Postfix(UI_ClueSubItem __instance, ClueManager.Clue clue)
            {
                if (!FeatureLoader.Enabled<ClueCardFeature>()) return;
                ClueCardBuilder.Apply(__instance, clue);
            }
        }

        /// <summary>
        /// 线索无效时原版会清空文本（<c>ClearInfo</c>，private，0.1.18 :86739）—— 我们那一格跟着清。
        /// </summary>
        /// <remarks>
        /// 不清的后果：列表复用时上一局的设备图像会留在这一行上，看起来像"这条线索指向那台设备"。
        /// </remarks>
        [HarmonyPatch(typeof(UI_ClueSubItem), "ClearInfo")]
        internal static class ClearInfoHook
        {
            private static void Postfix(UI_ClueSubItem __instance)
            {
                if (!FeatureLoader.Enabled<ClueCardFeature>()) return;
                ClueCardBuilder.Clear(__instance);
            }
        }

        /// <summary>
        /// 鼠标进入某一行（<c>OnEnterClue</c>，private，0.1.18 :86535）→ 换成整句视图。
        /// </summary>
        /// <remarks>
        /// 这个方法只由 <c>InitTabletClue</c> 绑到整条 <c>MousePanel</c> 上（0.1.18 :86516），
        /// 所以只有平板线索列表里的行会走它 —— 与旧版口径一致。
        /// </remarks>
        [HarmonyPatch(typeof(UI_ClueSubItem), "OnEnterClue")]
        internal static class EnterHook
        {
            private static void Postfix(UI_ClueSubItem __instance)
            {
                if (!FeatureLoader.Enabled<ClueCardFeature>()) return;
                ClueCardBuilder.SetHover(__instance, true);
            }
        }

        /// <summary>
        /// 鼠标离开（<c>OnExitClue</c>，private，0.1.18 :86545）→ 换回图标视图。
        /// </summary>
        [HarmonyPatch(typeof(UI_ClueSubItem), "OnExitClue")]
        internal static class ExitHook
        {
            private static void Postfix(UI_ClueSubItem __instance)
            {
                if (!FeatureLoader.Enabled<ClueCardFeature>()) return;
                ClueCardBuilder.SetHover(__instance, false);
            }
        }
    }
}
