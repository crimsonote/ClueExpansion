using System.Collections.Generic;
using Protocol;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 破坏点的生命周期编排：创建、假主人维护、破坏 / 修复会话、与电闸共用的 CD。
    /// </summary>
    /// <remarks>
    /// 全部状态都在服务端内存里；设备本身是普通的 <c>Server.Game.Device</c>，
    /// 所以换局时会被 <c>DeviceManager.Init()</c> 连带清掉 —— 重建挂在
    /// <c>StartFuseboxSabotage</c> 的 Postfix 上（开局与电闸重派发都会经过那里）。
    /// </remarks>
    internal static partial class ShadowSabotageFeature
    {
        // ══════════════════ 时间 ══════════════════

        /// <summary>本局生存计时（秒）。所有节流与进度都基于它，跨局自动归零。</summary>
        private static float Now
        {
            get
            {
                var tm = ServerTimeManager.Instance;
                return tm != null ? tm.SurviveTime : 0f;
            }
        }

        // ══════════════════ 设备创建与查找 ══════════════════

        /// <summary>
        /// 确保两只破坏点都存在（已存在就不动）。
        /// </summary>
        /// <remarks>
        /// 挂点语义：<c>DeviceManager.StartFuseboxSabotage</c>（0.1.18 :166828）被调用的时机
        /// 恰好是「开局」（<c>InitDevices</c>，:167336）与「供电全恢复 + 60 秒后重新派发」
        /// （<c>AreaManager.RefreshLight</c> 的 case 0，:176585）—— 后者就是我们与电闸共用的那条 CD。
        /// </remarks>
        internal static void EnsureDevices()
        {
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return;
            }

            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                EnsureOne(dm, BreakerStates.All[i]);
            }
        }

        private static BreakerDevice EnsureOne(ServerDeviceManager dm, BreakerState state)
        {
            var device = FindDevice(dm, state);
            if (device != null)
            {
                return device;
            }

            float x = state.Kind == BreakerKind.Identify ? IdentifyX : ClockX;
            float y = state.Kind == BreakerKind.Identify ? IdentifyY : ClockY;

            try
            {
                device = new BreakerDevice(state.Kind, state.DeviceId, x, y);

                // ⚠ SpawnDevice 的 isBroadcast 是死参数，它**总是**广播 S_SPAWN_DEVICE
                //   ⇒ 一创建全场可见（入场动画 + RuneSfx）。这是自建设备的固有代价。
                if (!dm.SpawnDevice(device))
                {
                    global::ClueDetail.Core.Log.Warn(
                        $"破坏点[{KindName(state)}] 生成失败：# {state.DeviceId} 已在设备表里。");
                    return null;
                }

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] 已生成：#{state.DeviceId} @({x:F0},{y:F0})。");
                return device;
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(
                    ex, $"破坏点[{KindName(state)}] 生成异常（坐标 {x:F0},{y:F0}）。");
                return null;
            }
        }

        /// <summary>按种类找设备（**按 <see cref="BreakerDevice.Kind"/> 而不是 ID**，以容忍改配置）。</summary>
        private static BreakerDevice FindDevice(ServerDeviceManager dm, BreakerState state)
        {
            if (dm == null || dm.Objects == null || state == null)
            {
                return null;
            }

            var list = dm.Objects;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is BreakerDevice d && d.Kind == state.Kind)
                {
                    return d;
                }
            }

            return null;
        }

        internal static BreakerDevice FindDevice(BreakerState state)
        {
            return FindDevice(ServerDeviceManager.Instance, state);
        }

        // ══════════════════ 窗口开始（= 电闸重派发） ══════════════════

        /// <summary>
        /// 新一轮「可破坏窗口」开启：确保设备存在、把两个点整点重置为正​​常且可破坏。
        /// </summary>
        /// <remarks>
        /// 这就是「与电闸共用 CD」的全部实现 —— **不自建任何计时器**。
        /// 原版那条 60 秒是：供电全部恢复 → 60 秒 → <c>RefreshLight</c> 的 case 0 重新派发 3 个可拆电闸；
        /// 这期间没有任何电闸可拆，那就是「CD 中」。我们挂在同一个信号上。
        /// <para>
        /// 语义对照：破坏 → <see cref="BreakerState.CanBreak"/> 置 false；
        /// **修复不会让它变回 true**，要等下一次重派发。所以「修复 ≠ 立刻能再破坏」。
        /// </para>
        /// </remarks>
        internal static void OnWindowStart()
        {
            EnsureDevices();

            var dm = ServerDeviceManager.Instance;
            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                var state = BreakerStates.All[i];
                AbortSession(state);

                state.CanBreak = true;
                state.Broken = false;
                state.BaseProgress = 0f;
                state.Progress = 0f;
                state.ArrowSent = false;
                state.ClueInjected = false;
                state.Faked.Clear();
                state.LastResend.Clear();

                var device = FindDevice(dm, state);
                if (device == null)
                {
                    continue;
                }

                SetSlot0(device, 0);
                BroadcastState(device);

                // 广播会把各客户端上的假主人冲掉 ⇒ 立刻按当前距离重算。
                RefreshFakes(device, state);

                // 可派发时**额外追加**这个点的「电闸」图钉（只发给黑幕）——
                // 与电闸同一套生命周期：能拆的时候黑幕在地图上看得见，拆掉就撤。
                SendPin(state, device, true);

                global::ClueDetail.Core.Log.Info($"破坏点[{KindName(state)}] 新一轮可破坏窗口开启（与电闸共用）。");
            }
        }

        // ══════════════════ 每帧 / 每秒维护 ══════════════════

        /// <summary>
        /// 每秒一次的例行维护：保活设备、维持「假主人」名单。
        /// </summary>
        /// <remarks>
        /// 破坏 / 修复的实时推进**不在这里**（1Hz 会让进度条一跳一跳），
        /// 而是由 <see cref="ScheduleBar"/> 的 100ms 自续链负责；这里只做兜底清理。
        /// </remarks>
        internal static void Tick()
        {
            var room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive || room.Players == null)
            {
                return;
            }

            var dm = ServerDeviceManager.Instance;
            if (dm == null)
            {
                return;
            }

            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                var state = BreakerStates.All[i];
                var device = FindDevice(dm, state);
                if (device == null)
                {
                    AbortSession(state);
                    state.Faked.Clear();
                    state.LastResend.Clear();
                    continue;
                }

                RefreshFakes(device, state);
            }
        }

        /// <summary>
        /// 维持「假主人」名单：**按距离加滞回 + 节流重发**。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么需要节流重发（而不是发一次就够）：小熊出生后约 1.5 秒内客户端
        /// <c>_isActivate</c> 还是 false，<c>GetInteractMessage</c> 会返回空（0.1.18 :4165）；
        /// 而且 <see cref="BroadcastState"/> 会把假主人冲掉。重发兜住这两种情况。
        /// </para>
        /// <para>
        /// 滞回：进 120 / 出 160。没有滞回的话，站在边界上的玩家会让提示反复闪烁
        /// （每次「收回」都要发一个真值包）。
        /// </para>
        /// </remarks>
        internal static void RefreshFakes(BreakerDevice device, BreakerState state)
        {
            var room = ServerGameRoom.Instance;
            if (device == null || state == null || room == null || room.Players == null)
            {
                return;
            }

            float now = Now;

            // ── ① 收回已经不该伪装的人（死了 / 躲柜子 / 不再是黑幕 / 已离场）──────
            if (state.Faked.Count > 0)
            {
                List<int> drop = null;
                foreach (int pid in state.Faked)
                {
                    var p = FindPlayer(room, pid);
                    if (p == null || !IsEligible(p, state))
                    {
                        drop = drop ?? new List<int>();
                        drop.Add(pid);
                    }
                }

                if (drop != null)
                {
                    for (int i = 0; i < drop.Count; i++)
                    {
                        int pid = drop[i];
                        var p = FindPlayer(room, pid);
                        if (p != null && p.Session != null)
                        {
                            SendTruthOwner(device, p);
                        }

                        state.Faked.Remove(pid);
                        state.LastResend.Remove(pid);
                    }
                }
            }

            // ── ② 按当前距离决定发 / 收 ────────────────────────────────────────
            // 注意：StateList[1] 是**单发**的，所以这里必须自己维护"谁被伪装了"。
            var players = room.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!IsEligible(p, state))
                {
                    continue;
                }

                int pid = p.PublicInfo.PlayerId;
                bool faked = state.Faked.Contains(pid);
                float limitSq = faked ? ExitRadiusSq : EnterRadiusSq;

                if (DistSq(p, device) <= limitSq)
                {
                    float last;
                    state.LastResend.TryGetValue(pid, out last);

                    if (!faked)
                    {
                        SendFakeOwner(device, p);
                        state.Faked.Add(pid);
                        state.LastResend[pid] = now;
                    }
                    else if (now - last >= ResendSeconds || now < last)
                    {
                        SendFakeOwner(device, p);
                        state.LastResend[pid] = now;
                    }
                }
                else if (faked)
                {
                    SendTruthOwner(device, p);
                    state.Faked.Remove(pid);
                    state.LastResend.Remove(pid);
                }
            }
        }

        /// <summary>把某个点的全部假主人收回（阶段切换 / 换局时用）。</summary>
        internal static void AbortFakes(BreakerState state)
        {
            var room = ServerGameRoom.Instance;
            var device = FindDevice(state);

            if (room != null && room.Players != null && device != null && state.Faked.Count > 0)
            {
                foreach (int pid in state.Faked)
                {
                    var p = FindPlayer(room, pid);
                    if (p != null && p.Session != null)
                    {
                        SendTruthOwner(device, p);
                    }
                }
            }

            state.Faked.Clear();
            state.LastResend.Clear();
        }

        // ══════════════════ 按 E ══════════════════

        /// <summary>
        /// 玩家在破坏点上按了 E。未破坏 ⇒ 开始破坏；已破坏 ⇒ 开始修复。
        /// </summary>
        internal static void OnInteract(BreakerDevice device, ServerGamePlayer player)
        {
            if (device == null || player == null || player.PublicInfo == null)
            {
                return;
            }

            var room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                return;
            }

            var state = BreakerStates.Of(device);
            if (state == null)
            {
                return;
            }

            if (!CanOperate(player, device, state))
            {
                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] 拒绝了 #{player.PublicInfo.PlayerId} 的交互" +
                    $"（已破坏={state.Broken}，可破坏={state.CanBreak}，颜色={player.Color}）。");
                return;
            }

            int pid = player.PublicInfo.PlayerId;

            // 幂等：同一个人连点时不重开会话（客户端 InteractLock 500ms，
            // 但按住不放仍可能连发）。
            if (state.SessionPid == pid && state.BarOn)
            {
                return;
            }

            if (state.SessionPid != 0)
            {
                EndSession(state);              // 换人：旧会话收尾，进度保留
            }

            state.SessionPid = pid;
            state.SessionStartAt = Now;
            state.Progress = state.BaseProgress;
            state.BarOn = false;
            state.LastBar = -1;

            int bar = ToBar(state.BaseProgress);
            if (BreakBar.Begin(player, BarItemId, bar))
            {
                state.BarOn = true;
                state.LastBar = bar;
                ScheduleBar(device, state, pid);

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] #{pid} 开始{(state.Broken ? "修复" : "破坏")}" +
                    $"（已有进度 {state.BaseProgress * 100f:F0}%）。");
            }
            else
            {
                // 发包失败：会话留着，下一秒 tick 会重试（客户端可能刚换场景）。
                global::ClueDetail.Core.Log.Warn($"破坏点[{KindName(state)}] #{pid} 进度条挂载失败，稍后重试。");
                state.SessionPid = 0;
            }
        }

        /// <summary>
        /// 按 E 是否成立。
        /// </summary>
        /// <remarks>
        /// 未破坏：只有黑幕（<c>EPlayerColor.Dark</c>，与原版拆电箱的守卫一致）且必须处于可破坏窗口；
        /// 已破坏：任何存活者都能修（修复**不受** CD 限制，只是修完仍不能立刻再破坏）。
        /// </remarks>
        private static bool CanOperate(ServerGamePlayer player, BreakerDevice device, BreakerState state)
        {
            if (!IsEligible(player, state))
            {
                return false;
            }

            return DistSq(player, device) <= EnterRadiusSq;
        }

        /// <summary>这个人此刻是否**有资格**操作这个破坏点（不含距离判定）。</summary>
        private static bool IsEligible(ServerGamePlayer player, BreakerState state)
        {
            if (player == null || player.PublicInfo == null || state == null)
            {
                return false;
            }

            if (!player.IsAlive || player.IsSpectator || player.PublicInfo.IsGhost)
            {
                return false;
            }

            if (player.State == EPlayerState.Hide || player.State == EPlayerState.Sit)
            {
                return false;
            }

            if (!state.Broken)
            {
                if (!state.CanBreak)
                {
                    return false;       // CD 中：连黑幕也不能碰
                }

                if (player.Color != EPlayerColor.Dark)
                {
                    return false;       // 未破坏时只有黑幕能拆
                }
            }

            return true;
        }

        // ══════════════════ 进度与完成 ══════════════════

        /// <summary>
        /// 100ms 自续链：推进进度、下发刻度、到点完成。
        /// </summary>
        /// <remarks>
        /// **链的终止完全靠自校验**，不需要任何取消句柄：
        /// 会话被移除（<see cref="EndSession"/>）/ 换人 / 操作者走了或死了 / 阶段变了
        /// ⇒ 任一不成立就不再排下一次，链自然断。延迟最多一个节拍（100ms）。
        /// </remarks>
        private static void ScheduleBar(BreakerDevice device, BreakerState state, int pid)
        {
            var room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            room.PushAfter(BarStepMs, delegate
            {
                if (state.SessionPid != pid || !state.BarOn)
                {
                    return;                             // 会话结束或换人 ⇒ 链断
                }

                var r = ServerGameRoom.Instance;
                var p = r != null ? FindPlayer(r, pid) : null;
                if (r == null || r.State != EGameState.Survive || p == null || !CanOperate(p, device, state))
                {
                    EndSession(state);                  // 离熊 / 死了 / 阶段变了 ⇒ 收尾（进度保留）
                    return;
                }

                float progress = state.BaseProgress + (Now - state.SessionStartAt) / BreakSecondsValue;
                if (progress >= 1f)
                {
                    Finish(device, state, p);
                    return;
                }

                state.Progress = progress;

                int bar = ToBar(progress);
                if (bar != state.LastBar)
                {
                    BreakBar.Set(pid, bar);
                    state.LastBar = bar;
                }

                ScheduleBar(device, state, pid);         // 排下一个
            });
        }

        /// <summary>进度满：切换破坏 / 修复状态。</summary>
        private static void Finish(BreakerDevice device, BreakerState state, ServerGamePlayer player)
        {
            int pid = player.PublicInfo.PlayerId;
            bool wasBroken = state.Broken;

            BreakBar.End(pid);

            state.BarOn = false;
            state.SessionPid = 0;
            state.SessionStartAt = 0f;
            state.BaseProgress = 0f;
            state.Progress = 0f;
            state.LastBar = -1;
            state.Broken = !wasBroken;

            if (state.Broken)
            {
                // 与电闸共用：破坏之后本轮不再可用，要等下一次重派发。
                state.CanBreak = false;

                // 时间污染要冻结的就是「破坏完成的那一刻」，必须在状态翻转处同步记下。
                var tm = ServerTimeManager.Instance;
                state.BrokenAt = tm != null ? tm.SurviveTime : 0;
            }

            SetSlot0(device, state.Broken ? 2 : 0);

            // 广播带出去的是 StateList[1] == 0 ⇒ 会冲掉所有假主人，强制重算。
            state.Faked.Clear();
            state.LastResend.Clear();
            BroadcastState(device);
            RefreshFakes(device, state);

            global::ClueDetail.Core.Log.Info(
                $"破坏点[{KindName(state)}] #{pid} 完成{(wasBroken ? "修复" : "破坏")}" +
                $"（可破坏={state.CanBreak}）。");

            OnStateChanged(device, state, wasBroken);
        }

        /// <summary>
        /// 结束当前会话并**保留进度**（离熊 / 换人 / 阶段切换）。
        /// </summary>
        private static void AbortSession(BreakerState state)
        {
            EndSession(state);
        }

        private static void EndSession(BreakerState state)
        {
            if (state == null)
            {
                return;
            }

            if (state.SessionPid != 0)
            {
                BreakBar.End(state.SessionPid);
            }

            if (state.Progress > state.BaseProgress)
            {
                state.BaseProgress = state.Progress;
            }

            state.SessionPid = 0;
            state.SessionStartAt = 0f;
            state.BarOn = false;
            state.LastBar = -1;
        }

        // ══════════════════ 工具 ══════════════════

        /// <summary>进度（0..1）→ 进度条刻度（0..24，客户端写死的上限）。</summary>
        private static int ToBar(float progress)
        {
            if (progress <= 0f)
            {
                return 0;
            }

            if (progress >= 1f)
            {
                return BarMax;
            }

            int value = (int)global::System.Math.Round(progress * BarMax);
            return value < 0 ? 0 : (value > BarMax ? BarMax : value);
        }

        private static void SetSlot0(BreakerDevice device, int value)
        {
            if (device == null || device.DeviceInfo == null || device.DeviceInfo.StateList == null)
            {
                return;
            }

            if (device.DeviceInfo.StateList.Count < 2)
            {
                return;
            }

            device.DeviceInfo.StateList[0] = value;
        }

        private static float DistSq(ServerGamePlayer player, BreakerDevice device)
        {
            if (player == null || player.PublicInfo == null || player.PublicInfo.Pos == null)
            {
                return float.MaxValue;
            }

            if (device == null || device.DeviceInfo == null || device.DeviceInfo.Pos == null)
            {
                return float.MaxValue;
            }

            float dx = player.PublicInfo.Pos.X - device.DeviceInfo.Pos.X;
            float dy = player.PublicInfo.Pos.Y - device.DeviceInfo.Pos.Y;
            return dx * dx + dy * dy;
        }

        private static ServerGamePlayer FindPlayer(ServerGameRoom room, int pid)
        {
            if (room == null || room.Players == null)
            {
                return null;
            }

            var players = room.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                {
                    return p;
                }
            }

            return null;
        }

        internal static string KindName(BreakerState state)
        {
            return state != null && state.Kind == BreakerKind.Clock ? "时钟记录组件" : "身份识别组件";
        }

        // ══════════════════ 状态翻转的扩展点 ══════════════════

        /// <summary>
        /// 破坏 / 修复状态刚翻转时的收口。
        /// </summary>
        /// <remarks>
        /// 分期实现的挂点，集中在这里可以保证「状态已经写好、广播已经发完」之后才动手：
        /// <list type="bullet">
        /// <item>P2：身份 / 时间线索污染的开关（污染是否生效由 <see cref="BreakerState.Broken"/> 与阶段共同判定）；</item>
        /// <item>P3：破坏 +10 秒给所有存活者发断电箭头；修复时撤箭头；撤 / 增电闸图钉；</item>
        /// <item>P4：往 <c>ClueList</c> 塞那条 <c>{11037, 破坏时刻, 房间}</c> 的线索
        /// —— 侦探阶段开始时原版 <c>Device.StartDetective</c> 会自动把 <c>Bubble</c> 置 -1。</item>
        /// </list>
        /// </remarks>
        /// <param name="device">破坏点设备。</param>
        /// <param name="state">它的运行期状态（此时 <see cref="BreakerState.Broken"/> 已是翻转后的新值）。</param>
        /// <param name="wasBroken">翻转前是否已破坏。</param>
        private static void OnStateChanged(BreakerDevice device, BreakerState state, bool wasBroken)
        {
            if (!wasBroken && state.Broken)
            {
                // ① 撤掉「这个点还能拆」的图钉（CD 中不该有它）。
                SendPin(state, device, false);

                // ② 延迟发箭头 —— 刻意不发任何文字通知。
                state.ArrowSent = false;
                ScheduleArrow(state, device);

                // ③ 把那条「黑幕切断了电力」塞进设备自己的线索表，
                //    让它留到调查阶段可被任何人扫描（见 InjectClue）。
                InjectClue(device, state);

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] 破坏生效：{PollutionSummary()}");
                return;
            }

            if (wasBroken && !state.Broken)
            {
                if (state.ArrowSent)
                {
                    BroadcastRemoveArrow(device);
                    state.ArrowSent = false;
                }

                // 保险：修复后仍在 CD 中，图钉不该在。
                SendPin(state, device, false);

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] 已修复：{PollutionSummary()}");
            }
        }

        /// <summary>
        /// 破坏后延迟 <see cref="ShadowSabotageFeature.NotifyDelaySeconds"/> 秒再发箭头。
        /// </summary>
        /// <remarks>
        /// ⚠ <c>TimeManager.PushSurvivalJob</c> 的回调**不随阶段清空**，
        /// 所以回调第一句必须自校验（见 <see cref="SendArrow"/>：<c>Broken</c> 与阶段都要查）。
        /// </remarks>
        private static void ScheduleArrow(BreakerState state, BreakerDevice device)
        {
            int delay = (int)global::System.Math.Round(NotifyDelayValue);

            if (delay <= 0)
            {
                SendArrow(state, device);
                return;
            }

            var tm = ServerTimeManager.Instance;
            if (tm == null)
            {
                SendArrow(state, device);
                return;
            }

            tm.PushSurvivalJob(delay, delegate
            {
                try
                {
                    SendArrow(state, device);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：延迟发送箭头失败。");
                }
            });
        }

        /// <summary>发箭头（带自校验：已修复 / 已发过 / 已离开 Survive 都不发）。</summary>
        private static void SendArrow(BreakerState state, BreakerDevice device)
        {
            if (state == null || !state.Broken || state.ArrowSent)
            {
                return;
            }

            var room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                return;
            }

            var target = FindDevice(state) ?? device;
            if (target == null)
            {
                return;
            }

            BroadcastArrow(target);
            state.ArrowSent = true;

            global::ClueDetail.Core.Log.Info(
                $"破坏点[{KindName(state)}] 已给所有存活者发出断电箭头（延迟 {NotifyDelayValue:F0} 秒）。");
        }

        /// <summary>
        /// 往破坏点的 <c>ClueList</c> 塞一条「黑幕切断了电力」的线索（P4）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>刻意不设 <c>DeviceInfo.Bubble = -1</c>。</b>两个理由：
        /// </para>
        /// <list type="number">
        /// <item>客户端 <c>DeviceBase.InteractBase</c>（0.1.18 :4843）与
        /// <c>GetInteractMessageBase</c>（:4880）都把 <c>Bubble == -1</c> 放在**第一优先** ——
        /// 一旦置上，按 E 就变成「调查」而**再也不能修复**。所以 Survive 期间必须保持
        /// <c>Bubble == 0</c>。</item>
        /// <item>根本不需要自己设：原版 <c>Device.StartDetective</c>（:166531）在侦探阶段开始时
        /// 会做 <c>ClueList.Count == 0 ? Bubble = 0 : Bubble = -1</c> 并广播 ——
        /// 只要这里塞了线索，到时候它自动就该可调查。</item>
        /// </list>
        /// <para>
        /// 渲染出来正是需求要的那句：<c>{ PlayerId = 11037, Time, RoomId }</c> ⇒
        /// 「黑幕 于 … 在 科学室 切断了电力」（11037 = <c>Define.MASTERMIND_SABOTAGE_ID</c>）。
        /// </para>
        /// <para>
        /// 跨局由原版负责：<c>Device.StartSurvival</c>（:166516）会 <c>ClueList.Clear()</c>
        /// 并发 <c>S_CLEAR_CLUES</c>；<see cref="BreakerState.ClueInjected"/> 也在窗口开始时复位。
        /// </para>
        /// </remarks>
        private static void InjectClue(BreakerDevice device, BreakerState state)
        {
            if (state.ClueInjected || device == null || device.ClueList == null)
            {
                return;
            }

            try
            {
                device.ClueList.Add(new PropositionInfo
                {
                    PlayerId = Define.MASTERMIND_SABOTAGE_ID,   // 11037 = 黑幕 + 切断电力
                    Time = state.BrokenAt,
                    RoomId = device.RoomID,
                });

                state.ClueInjected = true;

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] 已注入可调查线索" +
                    $"（黑幕 / {state.BrokenAt} 秒 / 房间 {device.RoomID}）。");
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "破坏点：注入可调查线索失败。");
            }
        }

        private static string PollutionSummary()
        {
            return $"身份污染={(IdentifyPollutionOn ? "开" : "关")}，时间污染={(TimePollutionOn ? "开" : "关")}" +
                   $"{(TimePollutionOn ? $"（冻结在 {FrozenTime} 秒）" : string.Empty)}。";
        }

        // ══════════════════ 线索污染的生效判定（P2） ══════════════════

        /// <summary>
        /// 身份识别组件是否正在污染线索身份 —— 破坏期间新写入的线索一律记成「某人」。
        /// </summary>
        /// <remarks>
        /// 条件：**已破坏** 且仍在本局的 Survive 阶段。
        /// <para>
        /// 阶段判定不能省：设备与 <see cref="BreakerState.Broken"/> 会一直留到调查阶段
        /// （那是刻意的，为了让破坏点本身可被扫描），但调查阶段本来就不再产生新线索，
        /// 也不该再改写任何东西。
        /// </para>
        /// </remarks>
        internal static bool IdentifyPollutionOn
        {
            get
            {
                if (!BreakerStates.Identify.Broken)
                {
                    return false;
                }

                var room = ServerGameRoom.Instance;
                return room != null && room.State == EGameState.Survive;
            }
        }

        /// <summary>时钟记录组件是否正在冻结线索时间。</summary>
        internal static bool TimePollutionOn
        {
            get
            {
                if (!BreakerStates.Clock.Broken)
                {
                    return false;
                }

                var room = ServerGameRoom.Instance;
                return room != null && room.State == EGameState.Survive;
            }
        }

        /// <summary>
        /// 时钟被破坏的时刻（<c>TimeManager.SurviveTime</c> 秒）。
        /// 时间污染把所有新线索的 <c>PropositionInfo.Time</c> 都改写成它。
        /// </summary>
        internal static int FrozenTime
        {
            get { return BreakerStates.Clock.BrokenAt; }
        }

        /// <summary>把时间污染施加到一条要发出去的线索上（只改副本，不动服务端权威对象）。</summary>
        internal static void ApplyFrozenTime(Protocol.PropositionInfo info)
        {
            if (info != null && TimePollutionOn)
            {
                info.Time = FrozenTime;
            }
        }
    }
}
