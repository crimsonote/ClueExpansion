using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using GameClueMirror = Server.Game.ClueMirror;

namespace ClueDetail.Features.Client.ClueRender
{
    internal partial class ClueRenderFeature
    {
        /// <summary>实际渲染用的命题句（平板线索卡、审判提案等走这里）。</summary>
        [HarmonyPatch(typeof(Util), nameof(Util.FormatProposition))]
        internal static class FormatHook
        {
            private static void Postfix(ClueManager.Clue clue, ref string __result)
            {
                if (!FeatureLoader.Enabled<ClueRenderFeature>()) return;
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
                if (!FeatureLoader.Enabled<ClueRenderFeature>()) return;
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
                if (!FeatureLoader.Enabled<ClueRenderFeature>()) return;
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
                if (!FeatureLoader.Enabled<ClueRenderFeature>()) return;
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
        /// 配对：在待响应队列里找**第一个房间匹配**的设备（2026-10-19 修 F5）。
        /// 旧实现只看队首 ⇒ 一次被拒（旁观者）或没响应的请求会**永久卡住队首**，
        /// 之后每个响应都对不上它、整轮配对失效（② 永远 miss，只能退到 ③/④ 猜房间）。
        /// 现在队列带 TTL（超时自动丢弃）并且是"搜索匹配"而不是"只认队首"。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ClueManager), nameof(ClueManager.TakeDeviceClue))]
        internal static class TakeDeviceClueHook
        {
            private static void Postfix(S_SCAN_DEVICE pkt)
            {
                if (!FeatureLoader.Enabled<ClueRenderFeature>()) return;
                if (pkt == null || pkt.InfoList == null)
                {
                    return;
                }


                if (pkt.InfoList.Count == 0)
                {
                    // 空响应不带房间号，没法校验：按"最老、且已过了扫描条时间"的那条记账。
                    ActivityMap.TakeScan();
                    return;
                }

                PropositionInfo first = pkt.InfoList[0];
                if (first == null)
                {
                    return;
                }

                // 在待响应队列里找**第一个房间匹配**的设备（不再只看队首）：单个错位不会拖垮整条队列。
                int deviceId = ActivityMap.TakeScanFor(first.RoomId);
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

        /// <summary>
        /// 还原 0.1.17a 的「活动也是线索身份的一部分」。
        /// </summary>
        /// <remarks>
        /// 原版 <c>AlreadyExistClue</c> 只用来决定 <c>TakeDeviceClue</c> 里"这条已经有了 ⇒ 不加"（:27593）；
        /// 0.1.17a 的判定是 <c>PlayerId &amp;&amp; RoomId &amp;&amp; Time &amp;&amp; WhatType</c>（:27570），
        /// 0.1.18 因为字段被删只剩前三项（:27611）。
        /// <para>
        /// 本补丁只在**原版已经判定"重复"**时介入：若那一批同名候选里**没有任何一条与它活动相同**，
        /// 就改判为"不存在"⇒ 调用方会另起一条，于是不同活动的线索各自保留。
        /// 活动身份取：未知字段 <b>100</b>（服务端下发的真实活动）→ 未知字段 <b>101</b>
        /// （破坏期标记，用显示时刻区分不同次破坏）→ <b>0</b>（都没有 ⇒ 与原版同口径，不会多出重复行）。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ClueManager), "AlreadyExistClue")]
        internal static class ActivityIdentityHook
        {
            private static void Postfix(ClueManager __instance, PropositionInfo info, ref bool __result)
            {
                if (!FeatureLoader.Enabled<ClueRenderFeature>()) return;
                if (!__result) return;
                if (ClueRenderFeature.DistinctByActivity == null ||
                    !ClueRenderFeature.DistinctByActivity.Value)
                {
                    return;
                }

                if (__instance == null || info == null || __instance.ClueList == null)
                {
                    return;
                }

                long mine = ActivityKey(info);
                for (int i = 0; i < __instance.ClueList.Count; i++)
                {
                    PropositionInfo clue = __instance.ClueList[i];
                    if (clue == null || clue.PlayerId != info.PlayerId ||
                        clue.RoomId != info.RoomId || clue.Time != info.Time)
                    {
                        continue;
                    }

                    if (ActivityKey(clue) == mine)
                    {
                        return;   // 有同活动的 ⇒ 确实是重复
                    }
                }

                __result = false;  // 只有活动不同的 ⇒ 按 0.1.17a 口径另起一条
            }

            /// <summary>活动身份：字段 100 优先，其次用破坏期标记（101 的显示时刻）。</summary>
            private static long ActivityKey(PropositionInfo info)
            {
                int what = UnknownFields.GetWhat(info);
                if (what != 0)
                {
                    return what;
                }

                int display = UnknownFields.GetDisplayTime(info);
                return display != 0 ? 100000L + display : 0L;
            }
        }
    }
}