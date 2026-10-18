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
        }
    }
}
