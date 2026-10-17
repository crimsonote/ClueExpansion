using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using GameClueMirror = Server.Game.ClueMirror;

namespace ClueDetail.Features.Client.ClueRender
{
    internal static partial class ClueRenderFeature
    {
        /// <summary>实际渲染用的命题句（平板线索卡、审判提案等走这里）。</summary>
        [HarmonyPatch(typeof(Util), nameof(Util.FormatProposition))]
        internal static class FormatHook
        {
            private static void Postfix(ClueManager.Clue clue, ref string __result)
            {
                if (clue != null && ClueWhat.TryDecorate(__result, clue, out string decorated))
                {
                    __result = decorated;
                }
            }
        }

        /// <summary>测试/调试用的命题句，口径与上面保持一致。</summary>
        [HarmonyPatch(typeof(Util), nameof(Util.TestFormatProposition))]
        internal static class TestFormatHook
        {
            private static void Postfix(ClueManager.Clue clue, ref string __result)
            {
                if (clue != null && ClueWhat.TryDecorate(__result, clue, out string decorated))
                {
                    __result = decorated;
                }
            }
        }

        /// <summary>
        /// 实时广播的线索（<c>S_ADD_CLUE</c>）**自带设备号**，直接接上 —— 这是最精确的一条路径。
        /// </summary>
        /// <remarks>
        /// <c>S_ADD_CLUE</c> 的字段是 <c>DeviceId = 1</c> + <c>Info = 2</c>（0.1.18 :145153），
        /// 客户端收到后进 <c>ClueMirror.Add(deviceId, info)</c>（:43626），而 <c>ClueMirror</c>
        /// 本身就是「设备 → 线索列表」的映射（:180791），连 <c>DeviceBase</c> 渲染线索都从它取
        /// （:5035 <c>ClueMirror.Get(ID)</c>）。
        /// <para>
        /// 所以"这条线索属于哪台设备"**客户端本来就知道** —— 只要在这里把
        /// <c>(playerId, time, roomId) → deviceId</c> 记一份，渲染时就能直接查到设备的
        /// <c>WhatType</c>，不需要去猜"房间里有什么设备"。
        /// </para>
        /// <para>
        /// 与扫描那条路的分工：<c>S_SCAN_DEVICE</c> 只带 <c>InfoList</c>、**没有**设备号（:127846），
        /// 只能靠"是我刚发的扫描请求"配对；而广播这条路是服务端明说的设备号，优先用它。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(GameClueMirror), nameof(GameClueMirror.Add))]
        internal static class ClueMirrorHook
        {
            private static void Prefix(int deviceId, PropositionInfo info)
            {
                if (info != null)
                {
                    ActivityMap.Remember(info.PlayerId, info.Time, info.RoomId, deviceId);
                }
            }
        }
        /// <summary>
        /// 本机发起扫描时把设备号压进待响应队列。
        /// </summary>
        /// <remarks>
        /// <c>DeviceBase.Scan</c> 是私有方法（0.1.18 反编译 :4952）：它启动 2 秒扫描条，
        /// 回调里发 <c>C_SCAN_DEVICE{DeviceId = ID}</c>。此处只记录 ID，不改任何行为。
        /// </remarks>
        [HarmonyPatch(typeof(DeviceBase), "Scan")]
        internal static class ScanRequestHook
        {
            private static void Prefix(DeviceBase __instance)
            {
                if (__instance != null)
                {
                    ActivityMap.QueueScan(__instance.ID);
                }
            }
        }

        /// <summary>
        /// 收到扫描结果时：① 读取房主侧状态标记；② 把这一批线索登记到队首那台设备上。
        /// </summary>
        /// <remarks>
        /// <c>S_SCAN_DEVICE</c> 只带 InfoList，协议里没有设备号 —— 所以"这次扫的是哪台设备"
        /// 只能由客户端自己在请求与响应之间搭桥。键与 <c>AlreadyExistClue</c> 的原版去重口径一致
        /// （PlayerId + RoomId + Time）。
        /// <para>
        /// 配对前先用房间号校验队首：请求被服务端拒掉（旁观者）时不会有响应，
        /// 那种情况下队首会与响应对不上，此时**不出队也不关联**，避免把整条队列错位。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ClueManager), nameof(ClueManager.TakeDeviceClue))]
        internal static class TakeDeviceClueHook
        {
            private static void Postfix(S_SCAN_DEVICE pkt)
            {
                if (pkt == null || pkt.InfoList == null)
                {
                    return;
                }


                if (pkt.InfoList.Count == 0)
                {
                    // 空响应（该设备没有线索）也要出队，否则队列会一直卡住。
                    ActivityMap.TakeScan();
                    return;
                }

                PropositionInfo first = pkt.InfoList[0];
                if (first == null || !ActivityMap.MatchesPendingRoom(first.RoomId))
                {
                    return;
                }

                int deviceId = ActivityMap.TakeScan();
                if (deviceId == 0)
                {
                    return;
                }

                foreach (PropositionInfo info in pkt.InfoList)
                {
                    if (info != null)
                    {
                        ActivityMap.Remember(info.PlayerId, info.Time, info.RoomId, deviceId);
                    }
                }
            }
        }
    }
}
