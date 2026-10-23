using System.Collections.Generic;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerCorpse = Server.Game.Corpse;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.CorpseMap
{
    internal partial class CorpseMapFeature
    {
        /// <summary>已经推过的尸体 id（同一具只推一次；藏尸 / 取出不会重推）。</summary>
        private static readonly HashSet<int> Notified = new HashSet<int>();

        /// <summary>换局清空（回大厅时调）。</summary>
        internal static void Reset()
        {
            Notified.Clear();
        }

        /// <summary>黑成功出刀（普通击杀）。</summary>
        [HarmonyPatch(typeof(ServerDeviceManager), nameof(ServerDeviceManager.CreateCorpse))]
        internal static class CreateCorpseHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerGamePlayer __0, ServerCorpse __result)
            {
                if (!FeatureLoader.Enabled<CorpseMapFeature>()) return;

                try
                {
                    Notify(__result, __0);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "案发点推送：处理普通击杀失败。");
                }
            }
        }

        /// <summary>黑成功出刀（把尸体直接藏进柜子 / 池塘）。</summary>
        [HarmonyPatch(typeof(ServerDeviceManager), nameof(ServerDeviceManager.CreateHiddenCorpse))]
        internal static class CreateHiddenCorpseHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerGamePlayer __0, ServerCorpse __result)
            {
                if (!FeatureLoader.Enabled<CorpseMapFeature>()) return;

                try
                {
                    Notify(__result, __0);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "案发点推送：处理藏尸击杀失败。");
                }
            }
        }

        /// <summary>
        /// 阶段切换：回大厅清空去重表；**进入调查阶段时撤掉生存阶段发的骷髅箭头**。
        /// </summary>
        /// <remarks>
        /// 为什么在调查阶段撤（2026-10-22 口径）：生存阶段箭头有用（知道自己杀了谁、死在哪，
        /// 便于决定藏尸 / 绕路）；调查阶段的位置情报应交回给"扫描尸体"，
        /// 而原版 <c>Corpse.Scan</c> 自己会发一条新的 <c>CorpseArrow</c>（:171964）——
        /// 旧箭头不撤就会和新箭头叠在一起、挡视线。而且 <c>UI_Arrow</c> 没有寿命，不撤会挂到换局。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class PhaseHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state, ServerGameRoom __instance)
            {
                if (!FeatureLoader.Enabled<CorpseMapFeature>()) return;

                try
                {
                    if (state == EGameState.Lobby)
                    {
                        Reset();
                        return;
                    }

                    if (state == EGameState.Detective)
                    {
                        ClearArrows(__instance ?? ServerGameRoom.Instance);
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "案发点通知：阶段切换清理失败。");
                }
            }
        }

        /// <summary>撤掉生存阶段发给黑方 / 黑幕的骷髅箭头（按死亡点广播撤销，没箭头的人是空操作）。</summary>
        private static void ClearArrows(ServerGameRoom room)
        {
            var dm = ServerDeviceManager.Instance;
            if (room == null || dm == null || dm.Corpses == null)
            {
                return;
            }

            int cleared = 0;
            for (int i = 0; i < dm.Corpses.Count; i++)
            {
                ServerCorpse corpse = dm.Corpses[i];
                if (corpse == null || corpse.DeadPos == null)
                {
                    continue;
                }

                // ⚠️ Pos 必须与发送时完全一致（发送用的是 corpse.DeadPos）；
                // 客户端 RemoveArrow 按坐标匹配，换成 DeviceInfo.Pos（差 ±100）就撤不掉。
                room.BroadcastAlivePlayers(new S_REMOVE_ARROW
                {
                    Type = EArrowType.CorpseArrow,
                    Pos = corpse.DeadPos.Clone(),
                });
                cleared++;
            }

            if (cleared > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"案发点通知：进入调查阶段，已撤掉 {cleared} 个骷髅箭头（位置情报交回扫描）。");
            }
        }

        /// <summary>给黑方 / 黑幕各推一份死者的案发点档案。</summary>
        /// <remarks>
        /// 只调原版 <c>Corpse.SendScanInfo</c>：拼包 + 单发，不改任何服务端状态。
        /// 判定口径与原版 <c>S_KILL_PLAYER</c> 一致（凶手不是死者本人且还活着，0.1.18 :167127），
        /// 所以自杀 / 炸弹尸体（<paramref name="black"/> 为 null）都会被挡掉。
        /// </remarks>
        private static void Notify(ServerCorpse corpse, ServerGamePlayer black)
        {
            var room = ServerGameRoom.Instance;
            if (corpse == null || corpse.DeviceInfo == null || black == null || black.PublicInfo == null)
            {
                return;
            }

            if (room == null || room.Players == null)
            {
                return;
            }

            // 与原版 CreateCorpse 的判定同源：凶手还活着、且不是死者本人。
            if (!black.IsAlive || black.PublicInfo.PlayerId == corpse.DeviceInfo.DeviceId)
            {
                return;
            }

            // 两个形态都关掉时直接不处理 —— 且必须在 Notified.Add 之前判断，
            // 否则热开之后会被"已通知过"挡住。
            bool toBlack = NotifyBlackValue;
            bool toMastermind = NotifyMastermindValue;
            bool arrow = SendArrowValue;
            bool file = SendFileValue;
            if (!arrow && !file)
            {
                return;
            }

            if (!Notified.Add(corpse.DeviceInfo.DeviceId))
            {
                return;
            }

            var mastermind = room.MasterMind;
            PosInfo arrowPos = corpse.DeadPos ?? corpse.DeviceInfo.Pos;

            int arrowed = 0;
            int filed = 0;
            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p == null || p.Session == null || p.PublicInfo == null || !p.IsAlive)
                {
                    continue;
                }

                bool dark = toBlack && (p.Color == EPlayerColor.Dark || p.Color == EPlayerColor.Black);
                bool mm = toMastermind && mastermind != null && p == mastermind;
                if (!dark && !mm)
                {
                    continue;
                }

                // 默认形态：只发箭头 —— 不动手持物品、不弹 UI、不怕施法打断。
                if (arrow && arrowPos != null)
                {
                    p.Session.Send(new S_NOTIFY_ARROW
                    {
                        Type = EArrowType.CorpseArrow,
                        Pos = arrowPos.Clone(),
                    });
                    arrowed++;
                }

                // 可选：平板档案（默认关，副作用见 Feature.cs 的 SendFile 注释）。
                if (file)
                {
                    corpse.SendScanInfo(p);
                    filed++;
                }
            }

            if (arrowed > 0 || filed > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"案发点通知：死者 #{corpse.DeviceInfo.DeviceId} —— 骷髅箭头 {arrowed} 份、平板档案 {filed} 份" +
                    $"（尸体仍保持未发现、可扫描状态）。");
            }
        }
    }
}
