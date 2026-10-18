using System.Collections.Generic;
using ClueDetail.Core.Net;
using ClueDetail.Features.Client.LieWhat;
using ClueDetail.Features.UI.LobbyToggle;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 启动自检：把"跨端通道能不能用、静态表读不读得到、房主装没装"打进日志。
    /// </summary>
    /// <remarks>
    /// 补丁挂不上、prefab 路径没命中、词条没注入 —— 这些在游戏里都表现为"功能没生效"，
    /// 光看画面分不清是哪一层断了。所以这里在数据表就绪后输出一次诊断，
    /// 其中**未知字段通道用真实的写入→读回自证**（不是查标志位）。
    /// <para>只读、只打日志，不改变任何行为；输出一次后不再重复。</para>
    /// </remarks>
    internal static class Diagnostics
    {
        private static bool _reported;

        public static void ReportOnce()
        {
            if (_reported)
            {
                return;
            }

            var data = Managers.Data;
            if (data == null || data.TextDic == null || data.TextDic.Count == 0)
            {
                return;
            }

            if (data.DeviceDic == null || data.DeviceDic.Count == 0 || data.RoomDic == null)
            {
                return;
            }

            _reported = true;

            Log.Info("──────── ClueDetail 自检 ────────");
            Log.Info($"语言 {Localization.CurrentLanguage()}；TextDic {data.TextDic.Count} 条；" +
                     $"DeviceDic {data.DeviceDic.Count}；RoomDic {data.RoomDic.Count}");

            Log.Info($"自定义协议：已注册 {CdWire.Registered}/{CdOpcode.Count} 条" +
                     (CdWire.Failed > 0 ? $"（失败 {CdWire.Failed}，见上面的错误日志）" : ""));
            Log.Info("未知字段通道：" + ProbeUnknownFields());
            Log.Info($"本机是房主：{Yes(RoomState.IsHost)}；房主开关：{DescribeRoomState()}");

            int roomId = FindRoomWithActivity();
            if (roomId != 0)
            {
                List<EWhatType> candidates = RoomDeviceScan.CandidatesOf(roomId);
                Log.Info($"按房间推断抽样：房间 {roomId} → {Describe(candidates)}（共 {candidates.Count} 种）");
            }
            else
            {
                Log.Warn("按房间推断抽样：没找到任何带活动的房间 —— 设备表可能异常。");
            }

            Log.Info("────────────────────────────────");
        }


        /// <summary>
        /// 端到端自证：真的往一条 PropositionInfo 写一个值，再读回来。
        /// </summary>
        /// <remarks>
        /// 这条反射链（<c>_unknownFields</c> → <c>fields</c> → <c>GetOrAddField</c> → <c>varintList</c>）
        /// 全是 protobuf 的私有实现细节，换版本可能断。断了必须**看得见**：
        /// 症状是"线索又没了最后一段"，而这行日志能直接把它和"补丁没挂上"区分开。
        /// </remarks>
        private static string ProbeUnknownFields()
        {
            var probe = new PropositionInfo();
            bool ok = UnknownFields.SetValue(probe, UnknownFields.WhatFieldNumber, 1)
                      && UnknownFields.GetWhat(probe) == 1;

            return ok
                ? "通过（写入→读回一致）"
                : "失败 —— 详细活动无法跨端传递（按房间推断的自采集仍然可用）";
        }
        private static string DescribeRoomState()
        {
            if (RoomState.IsHost)
            {
                return RoomState.HostBroadcast ? "开（本机就是房主）" : "关（本机房主已关闭）";
            }

            if (!RoomState.RemoteKnown)
            {
                return "未收到标记 —— 房主没装本模块，将走客户端自采集";
            }

            return RoomState.RemoteEnabled ? "开（跟随服务端）" : "关（跟随服务端）";
        }

        private static string Describe(List<EWhatType> candidates)
        {
            if (candidates.Count == 0)
            {
                return "无";
            }

            var parts = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                parts.Add(candidates[i].ToString());
            }

            return string.Join(", ", parts.ToArray());
        }

        private static int FindRoomWithActivity()
        {
            foreach (var pair in Managers.Data.DeviceDic)
            {
                var device = pair.Value;
                if (device == null || device.WhatType == EWhatType.WNone)
                {
                    continue;
                }

                foreach (var room in Managers.Data.RoomDic)
                {
                    if (room.Value != null && room.Value.Type == device.RoomType)
                    {
                        return room.Key;
                    }
                }
            }

            return 0;
        }

        private static string Yes(bool value)
        {
            return value ? "是" : "否";
        }

        /// <summary>UI 建立情况（供自检之外的手动排查用）。</summary>
        public static string UiState()
        {
            return $"设置页开关={(LobbyToggle.IsBuilt ? "已建立" : "未建立")}，" +
                   $"伪证活动行={(LieWhatRow.IsBuilt ? "已建立" : "未建立")}";
        }

        // ══════════════════ 线索解析追踪（2026-10-18 实机排查加） ══════════════════
        // 症状是「卡片显示的设备不对 / 悬浮整句里没有活动」时，光看画面分不清断在哪一环：
        // 包内未知字段、设备关联、房间推断，还是三个都没命中。这里把每条线索的**来源**打出来。
        // 默认开、每条链路最多 40 行 —— 排查够用，也不会把日志刷爆。

        /// <summary>是否打印线索解析追踪（绑定自 <c>[ClueRender] LogResolution</c>）。</summary>
        public static bool LogClueResolution { get; set; } = true;

        private const int TraceLimit = 40;

        private static int _clueTraces;
        private static int _hostTraces;

        /// <summary>客户端侧：这条线索的详细活动是从哪一级拿到的。</summary>
        public static void TraceClue(int roomId, int playerId, int time, int what, bool ambiguous, string source)
        {
            if (!LogClueResolution || _clueTraces >= TraceLimit)
            {
                return;
            }

            _clueTraces++;
            string text = what == 0 ? "-" : ((EWhatType)what).ToString();
            Log.Info(
                $"线索解析[{_clueTraces}/{TraceLimit}] 房间{roomId} 玩家{playerId} 时间{time}" +
                $" → 活动 {text}{(ambiguous ? "（歧义）" : "")} 来源={source}");
        }

        /// <summary>房主侧：这条要广播的线索有没有在旁路表里命中。</summary>
        public static void TraceHost(int playerId, int time, int roomId, int what, bool hit)
        {
            if (!LogClueResolution || _hostTraces >= TraceLimit)
            {
                return;
            }

            _hostTraces++;
            string text = hit ? ((EWhatType)what).ToString() : "-";
            Log.Info(
                $"房主下发[{_hostTraces}/{TraceLimit}] 玩家{playerId} 时间{time} 房间{roomId}" +
                $" → 活动 {text}（旁路表{(hit ? "命中" : "没命中")}）");
        }
    }
}
