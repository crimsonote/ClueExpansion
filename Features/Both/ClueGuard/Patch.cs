using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using GameHostPeerSession = Server.Game.HostPeerSession;

namespace ClueDetail.Features.Both.ClueGuard
{
    internal partial class ClueGuardFeature
    {
        /// <summary>
        /// 房主侧：整批下发（<c>S_ADD_MY_CLUES</c>）时剔除 <c>Destroyed</c> 线索。
        /// </summary>
        /// <remarks>
        /// 原版 <c>StartDetective</c> 的活人分支不判断 <c>Destroyed</c>；这里在
        /// <c>HostPeerSession.Send</c> 上把包换成过滤后的副本。
        /// ⚠️ 这是服务端行为改动，未装本模块的客户端也受影响（默认关）。
        /// </remarks>
        [HarmonyPatch(typeof(GameHostPeerSession), "Send", new[] { typeof(IMessage) })]
        internal static class HostBatchFilterHook
        {
            private static void Prefix(ref IMessage packet)
            {
                if (!FeatureLoader.Enabled<ClueGuardFeature>()) return;
                if (FilterHostBatch == null || !FilterHostBatch.Value) return;
                if (!(packet is S_ADD_MY_CLUES mine) || mine.InfoList == null) return;

                bool hasDestroyed = false;
                for (int i = 0; i < mine.InfoList.Count; i++)
                {
                    if (mine.InfoList[i] != null && mine.InfoList[i].Destroyed)
                    {
                        hasDestroyed = true;
                        break;
                    }
                }

                if (!hasDestroyed)
                {
                    return;
                }

                S_ADD_MY_CLUES copy = mine.Clone();
                int removed = 0;
                for (int i = copy.InfoList.Count - 1; i >= 0; i--)
                {
                    if (copy.InfoList[i] != null && copy.InfoList[i].Destroyed)
                    {
                        copy.InfoList.RemoveAt(i);
                        removed++;
                    }
                }

                packet = copy;
                global::ClueDetail.Core.Log.Info(
                    $"线索守卫：整批下发剔除 {removed} 条已销毁线索（剩余 {copy.InfoList.Count} 条）。");
            }
        }

        /// <summary>客户端侧：平板列表拒收 <c>Destroyed</c> 线索。</summary>
        [HarmonyPatch(typeof(ClueManager), nameof(ClueManager.AddClue))]
        internal static class ClientListFilterHook
        {
            private static bool Prefix(PropositionInfo info)
            {
                if (!FeatureLoader.Enabled<ClueGuardFeature>()) return true;
                if (FilterClientList == null || !FilterClientList.Value) return true;
                if (info == null || !info.Destroyed) return true;

                global::ClueDetail.Core.Log.Info(
                    $"线索守卫：客户端拒绝已销毁的线索（玩家{info.PlayerId} 时间{info.Time} 房间{info.RoomId}）。");
                return false;
            }
        }
    }
}
