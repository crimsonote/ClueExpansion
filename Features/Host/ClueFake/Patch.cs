using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.ClueFake
{
    internal partial class ClueFakeFeature
    {
        /// <summary>
        /// <c>x</c> 重写槽的应用点：在 <c>S_ADD_CLUE</c> 真正广播出去之前改写它的时间与人物。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么挂在 <c>GameRoom.Broadcast</c> 而不是 <c>RecordLastUsingPlayer</c>：
        /// 后者自 2026-10-20 起被 <c>ClueMerge</c> **接管**（Prefix 返回 false，Postfix 不跑），
        /// 而 <c>ClueMerge</c> 本身也可能被用户在配置里关掉（那时走原版方法）。
        /// 两个分支最后都会调用 <c>Broadcast</c>，所以这里是唯一稳定的收口。
        /// </para>
        /// <para>
        /// ⚠️ <c>[HarmonyPriority(Priority.Low)]</c>：要让本模块其它 <c>Broadcast</c> 补丁
        /// （<c>ShadowSabotage</c> 的时间/身份污染改写）先跑，重写发生在**最外层**，
        /// 否则可能被后续改写覆盖。
        /// </para>
        /// <para>
        /// ⚠️ 只改**包内副本 + 服务端 <c>ClueList</c> 对象**，不动 <c>RecordLastUsingPlayer</c>
        /// 的合并窗口判断（`x` 是一次性技能，见 <see cref="ClueFakeLogic.ApplyRewrite"/>）。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class RewriteHook
        {
            [HarmonyPrefix]
            private static void Prefix(IMessage packet)
            {
                if (!FeatureLoader.Enabled<ClueFakeFeature>()) return;

                var add = packet as S_ADD_CLUE;
                if (add != null)
                {
                    ClueFakeLogic.ApplyRewrite(add);
                }
            }
        }

        /// <summary>
        /// 阶段切换：离开生存阶段清会话 / 重写槽；回大厅整局清零（线索不跨局）。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class PhaseHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (!FeatureLoader.Enabled<ClueFakeFeature>()) return;

                try
                {
                    if (state == EGameState.Lobby)
                    {
                        ClueFakeState.ResetRound();
                    }
                    else if (state != EGameState.Survive)
                    {
                        ClueFakeState.ClearSessions();
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "线索伪造：阶段清理失败（已安全忽略）。");
                }
            }
        }
    }
}
