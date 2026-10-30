using System;
using Protocol;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.MissionBacklash
{
    /// <summary>
    /// 任务倒扣的记账与结算。全部在 Unity 主线程执行（<c>SurvivalTick</c> 的 Postfix）。
    /// </summary>
    /// <remarks>
    /// 数值口径逐字复刻 <c>MissionManager.ClearMission</c>（0.1.18 :169575-169613）：
    /// <list type="bullet">
    /// <item>进度 = <c>|Point| × EscapeGaugeWeight × 0.8</c>（:169591-169598）；</item>
    /// <item>时间 = <c>|Point| × (15 / PublicRemainPlayerCount) × TimeLimitIncreaseWeight</c>
    /// （:169607-169613，注意 <c>15 / 存活人数</c> 是**整数除法**）；</item>
    /// <item>负分任务（如充电占位）取绝对值 —— 原版也是取绝对值后加。</item>
    /// </list>
    /// </remarks>
    internal partial class MissionBacklashFeature
    {
        /// <summary>每秒维护：对齐周期、到点结算。挂在 <c>GameRoom.SurvivalTick</c> 的 Postfix。</summary>
        internal static void Tick()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                // 非生存阶段（大厅 / 审判 / 结算）不记账，也不保留上一局的残账。
                Reset();
                return;
            }

            if (!MissionBridge.Ready)
            {
                return;
            }

            object manager = MissionBridge.Instance;
            if (manager == null)
            {
                return;
            }

            // 进度已满（全班逃生已触发）之后不再扣，避免和原版收尾流程打架。
            if (MissionBridge.AllClear(manager))
            {
                return;
            }

            ServerTimeManager time = ServerTimeManager.Instance;
            if (time == null)
            {
                return;
            }

            long now = time.SurviveTime;

            // 只在**游戏时钟整点**结算（1 小时 = 60 秒）：7:00 / 8:00 / 9:00……
            // 早前按「首次 Tick 对齐」实现会带入局偏移（421 ⇒ 8:01、9:01……），已废弃。
            if (now <= 0 || now % 60 != 0)
            {
                return;
            }

            long hour = now / 60;
            int every = EveryHoursValue;
            if (hour % every != 0)
            {
                return;
            }

            // 同一个整点只结算一次（防止同一秒被重复调用）。
            if (hour == LastSettledHour)
            {
                return;
            }

            LastSettledHour = hour;
            Settle(manager);
        }

        /// <summary>
        /// 记录一个由黑方 / 黑幕完成的任务所「本应增加」的进度与时间。
        /// 由 <c>ClearMission</c> 的 Postfix 在确认任务真的被清掉之后调用。
        /// </summary>
        /// <remarks>
        /// ⚠️ 这里**只是记账**：原版在 <c>ClearMission</c> 方法体里已经把进度与时间加过了
        /// （:169600 / :169613），本方法不改也不回滚那两笔；真正的扣减只在周期边界由
        /// <see cref="Settle"/> 执行。
        /// </remarks>
        internal static void Record(object manager, ESchoolMission mission)
        {
            if (manager == null)
            {
                return;
            }

            Data.MissionData data = Managers.Data.GetMissionData((int)mission);
            if (data == null)
            {
                return;
            }

            int point = data.Point < 0 ? -data.Point : data.Point;
            if (point <= 0)
            {
                return;
            }

            float progress = point * MissionBridge.EscapeGaugeWeight(manager) * 0.8f;

            // 原版是整数除法 15 / PublicRemainPlayerCount（:169612），这里逐字复刻。
            int remain = MissionBridge.PublicRemainPlayerCount(manager);
            if (remain <= 0)
            {
                remain = 1;
            }

            float seconds = point * (float)(15 / remain) * MissionBridge.TimeLimitIncreaseWeight(manager);

            // 难度预设 / 反射读出来的权重理论上都是有限正数；一旦拿到 NaN / Infinity，
            // 累加进去会让整个桶变成 NaN，进而在结算时把进度写成 NaN。宁可丢弃这一笔。
            if (!IsUsable(progress))
            {
                progress = 0f;
            }

            if (!IsUsable(seconds))
            {
                seconds = 0f;
            }

            if (progress <= 0f && seconds <= 0f)
            {
                return;
            }

            PendingProgress += progress;
            PendingSeconds += seconds;

            global::ClueDetail.Core.Log.Info(
                $"任务倒扣：记录 {mission}（黑方完成），应加 {progress:0.##} 进度 / {seconds:0.##} 秒；" +
                $"本周期累计 {PendingProgress:0.##} / {PendingSeconds:0.##}。");
        }

        /// <summary>结算上一周期：按倍率扣掉记录的进度与时间。</summary>
        private static void Settle(object manager)
        {
            double progress = PendingProgress;
            double seconds = PendingSeconds;
            PendingProgress = 0d;
            PendingSeconds = 0d;

            if (progress <= 0d && seconds <= 0d)
            {
                return;
            }

            float mult = MultiplierValue;

            if (PenalizeProgressValue && progress > 0d)
            {
                DeductProgress(manager, (float)(progress * mult));
            }

            if (PenalizeTimeValue && seconds > 0d)
            {
                DeductTime((float)(seconds * mult));
            }
        }

        private static void DeductProgress(object manager, float amount)
        {
            if (!IsUsable(amount))
            {
                return;
            }

            float goal = MissionBridge.GoalPoint(manager);
            if (!IsUsable(goal))
            {
                return;
            }

            float current = MissionBridge.CurrentPoint(manager);
            if (float.IsNaN(current) || float.IsInfinity(current))
            {
                // 进度已经是脏值，再写只会把脏值固化成 NaN。
                return;
            }

            // 下限 0：进度只会被抹平，不会变成负数（原版 CurrentPoint 也始终 ≥ 0）。
            float next = Math.Max(0f, current - amount);
            if (!MissionBridge.SetCurrentPoint(manager, next))
            {
                return;
            }

            int percent = (int)(next / goal * 100f);
            ServerGameRoom.Instance?.Broadcast(new S_MISSION_PROGRESS_PERCENT { Percent = percent });

            global::ClueDetail.Core.Log.Info(
                $"任务倒扣：结算进度 {current:0.##} → {next:0.##}（-{amount:0.##}，{percent}%）。");
        }

        private static void DeductTime(float seconds)
        {
            if (!IsUsable(seconds))
            {
                return;
            }

            ServerTimeManager time = ServerTimeManager.Instance;
            if (time == null)
            {
                return;
            }

            // 原版 UpdateRemainTime 内部是 Math.Clamp(RemainTime + delta, 0f, TotalTime)（:181705），
            // 扣到 0 就停住，不会出现负倒计时。
            time.UpdateRemainTime(-seconds);

            global::ClueDetail.Core.Log.Info(
                $"任务倒扣：结算倒计时 -{seconds:0.##} 秒（剩余 {time.RemainTime:0}）。");
        }

        /// <summary>有限的正数：排除 NaN / ±Infinity / 0 / 负数。</summary>
        private static bool IsUsable(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }
    }
}
