using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimelineRecorder = Server.Game.TimelineRecorder;

namespace ClueDetail.Features.Host.ShootReminder
{
    internal partial class ShootReminderFeature
    {
        /// <summary>回放帧发送（= 拍摄时刻）后触发变暗提醒。</summary>
        /// <remarks>
        /// 挂 Postfix 而不是 Prefix：<c>Send</c> 在时钟破坏期间会被
        /// <c>ShadowSabotage</c> 的 Prefix 拦下（返回 false），那时 Postfix 不执行 ——
        /// 正好符合「变暗提醒与时钟破坏无关」的口径。
        /// </remarks>
        [HarmonyPatch(typeof(ServerTimelineRecorder), "Send", new[] { typeof(TimelineFrame) })]
        internal static class SendHook
        {
            [HarmonyPostfix]
            private static void Postfix(TimelineFrame frame)
            {
                if (!FeatureLoader.Enabled<ShootReminderFeature>()) return;

                try
                {
                    OnFrame(frame);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "拍摄提醒：处理回放帧失败。");
                }
            }
        }

        /// <summary>每帧判断是否命中「每小时第 2 次拍摄」，命中就给目标玩家发变暗。</summary>
        private static void OnFrame(TimelineFrame frame)
        {
            if (frame == null)
            {
                return;
            }

            ServerTimelineRecorder recorder = ServerTimelineRecorder.Instance;
            if (recorder == null)
            {
                return;
            }

            int rel = frame.Time - recorder.Offset;
            if (rel < 0 || rel % 60 != 10)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive || room.Players == null)
            {
                return;
            }

            bool all = ToAllValue;
            EEffectType effect = EffectChoiceValue == 1 ? EEffectType.MineBombVfx : EEffectType.BlackOutVfx;
            ServerGamePlayer mastermind = room.MasterMind;
            int sent = 0;

            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p == null || p.Session == null || p.PublicInfo == null || !p.IsAlive)
                {
                    continue;
                }

                bool hit = all
                    || p.Color == EPlayerColor.Dark
                    || p.Color == EPlayerColor.Black
                    || (mastermind != null && p == mastermind);
                if (!hit)
                {
                    continue;
                }

                p.Session.Send(new S_PLAY_EFFECT
                {
                    Type = effect,
                });
                sent++;
            }

            if (sent > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"拍摄提醒：帧 t={frame.Time}（拍摄偏移 {recorder.Offset}）→ 变暗 {sent} 人（{effect}）。");
            }
        }
    }
}
