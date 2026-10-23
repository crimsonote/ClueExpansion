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
    internal partial class ShadowSabotageFeature
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
        /// <summary>这个破坏点在配置里是否启用（2026-10-20 新增：两个点独立开关）。</summary>
        internal static bool KindEnabled(BreakerKind kind)
        {
            return kind == BreakerKind.Clock ? EnableClockValue : EnableIdentifyValue;
        }

        internal static void EnsureDevices()
        {
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return;
            }

            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                // 关掉的那个点：如果之前已经生成过，要撤掉（热开关）。
                if (!KindEnabled(BreakerStates.All[i].Kind))
                {
                    RemoveOne(dm, BreakerStates.All[i]);
                    continue;
                }

                EnsureOne(dm, BreakerStates.All[i]);
            }
        }

        /// <summary>撤掉某个破坏点（配置里关掉它时走这里，热开关）。</summary>
        private static void RemoveOne(ServerDeviceManager dm, BreakerState state)
        {
            BreakerDevice device = FindDevice(dm, state);
            if (device == null)
            {
                return;
            }

            try
            {
                if (state.ArrowSent)
                {
                    BroadcastRemoveArrow(device);
                    state.ArrowSent = false;
                }

                SendPin(state, device, false);
                dm.RemoveDevice(device);
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(
                    ex, $"破坏点：撤除[{KindName(state)}]失败（可忽略）。");
            }
            finally
            {
                BreakerStates.ResetOne(state);
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

        /// <summary>
        /// **实验性后门**：不经玩家操作，直接把某个破坏点强制置为「已破坏 / 已修复」。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 走的是与玩家交互**完全相同**的收口：<see cref="Finish"/> →
        /// <c>SetSlot0</c> / <c>BroadcastState</c> / <c>RefreshFakes</c> / <see cref="OnStateChanged"/>
        /// （封锁其他破坏、发箭头、记 11037 线索、修复后 60 秒重派发全都照旧）。
        /// 差别只有两点：没有进度条（<c>pid == 0</c>），以及不走 <c>CanOperate</c> 的角色门禁。
        /// </para>
        /// <para>
        /// 只由 <c>Features/Both/CommandBridge</c> 的 <c>cd_sabotage</c> 调用；
        /// 房主门禁天然成立 —— <c>ServerGameRoom.Instance</c> 只在服务端存在。
        /// </para>
        /// </remarks>
        /// <summary>
        /// 给命令桥看的**只读**状态行：两个破坏点各自的状态 + 当前污染开关。
        /// </summary>
        internal static string StatusText()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            var sb = new global::System.Text.StringBuilder();
            sb.Append("破坏点状态（").Append(room == null ? "非服务端" : room.State.ToString()).Append("）：");
            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                BreakerState s = BreakerStates.All[i];
                sb.Append("\n  · ").Append(KindName(s))
                  .Append("：").Append(s.Broken ? "已破坏" : "完好")
                  .Append("，可破坏=").Append(s.CanBreak)
                  .Append("，设备 #").Append(s.DeviceId);
            }

            sb.Append("\n  · ").Append(PollutionSummary());
            return sb.ToString();
        }

        internal static bool ForceToggle(BreakerKind kind, bool broken, out string message)
        {
            var room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                message = "只能在生存阶段（Survive）里控制破坏点；当前不在服务端或不在生存阶段。";
                return false;
            }

            BreakerState state = BreakerStates.Of(kind);
            BreakerDevice device = FindDevice(state);
            if (device == null)
            {
                EnsureDevices();
                device = FindDevice(state);
            }

            if (device == null)
            {
                message = $"破坏点[{KindName(state)}]尚未生成（本局的破坏窗口可能还没派发）。";
                return false;
            }

            if (state.Broken == broken)
            {
                message = $"破坏点[{KindName(state)}]已经是「{(broken ? "已破坏" : "完好")}」状态，无需改动。" +
                          PollutionSummary();
                return true;
            }

            AbortSession(state);        // 若正被某个玩家操作，先收尾（进度作废）
            Finish(device, state, null);
            message = $"破坏点[{KindName(state)}] 已强制{(broken ? "破坏" : "修复")}。" + PollutionSummary();
            return true;
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
            var dm = ServerDeviceManager.Instance;

            // 同一个挂点承担两种语义，必须分开：
            // ① 开局（InitDevices）：DeviceManager.Init() 刚把我们的设备清掉 ⇒ FindDevice 为 null ⇒ 新一局，无条件复位；
            // ② 电闸重派发（RefreshLight case 0）：设备还在 ⇒ **尊重「有人正在破坏」的封锁**
            //    （2026-10-18 口径：破坏期间不可进行其他破坏，电闸也不行）。
            bool freshRound = dm == null || FindDevice(dm, BreakerStates.Identify) == null;

            EnsureDevices();

            if (freshRound)
            {
                // 新一局：上一局的污染窗口必须清掉。否则 IsPollutedRecord 会把本局记录
                // 误判成上一局污染（典型症状：本局只做了身份破坏，镜子的时间却被上一局
                // 时钟破坏时刻冻结；或身份脸一直轮换停不下来）。
                BreakerStates.MirrorResidual.Clear();
                BreakerStates.LastMirrorCycle = 0f;
            }

            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                var state = BreakerStates.All[i];

                if (!freshRound && state.Broken)
                {
                    // 还没被修好：这一次窗口开启作废（连进行中的修复会话都不动它）。
                    global::ClueDetail.Core.Log.Info(
                        $"破坏点[{KindName(state)}] 仍在破坏中：拒绝这次窗口开启（修复完成 + {ReloadSeconds} 秒后才重派发）。");
                    continue;
                }

                AbortSession(state);

                if (freshRound)
                {
                    // 同上：跨局残留的 BrokenAt / RepairedAt 会让 IsPollutedRecord 误判。
                    state.BrokenAt = 0;
                    state.RepairedAt = 0;
                }

                state.CanBreak = true;
                state.Broken = false;
                state.BaseProgress = 0f;
                state.Progress = 0f;
                state.ArrowSent = false;
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

            // 上面被 continue 掉的点仍在破坏 ⇒ 刚被原版重新派发的电闸要立刻收回去。
            if (!freshRound && BreakerStates.AnyBroken)
            {
                CloseFuseboxWindow();
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

                EnsurePosition(dm, state, device);
                RefreshFakes(device, state);
            }

            // 身份破坏期间：镜子里的脸每秒轮换一次（只改展示，真值不动）。
            try
            {
                CycleMirrors();
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "破坏点：镜子脸轮换失败。");
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
            if (state.Sessions.ContainsKey(pid))
            {
                return;
            }

            // ⚠️ 2026-10-20：**不再**顶掉别人的会话。多人可以同时修，谁先跑满谁完成
            //（对齐原版电箱：客户端各自 StartCasting，服务端 SaveRepairTick 只取最大 tick）。
            int bar = ToBar(state.BaseProgress);
            if (BreakBar.Begin(player, BarItemId, bar))
            {
                state.Sessions[pid] = Now;
                state.LastBar[pid] = bar;
                ScheduleBar(device, state, pid);
                SetBusyState(player, state.Broken ? WhiteActionValue : BlackActionValue);

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] #{pid} 开始{(state.Broken ? "修复" : "破坏")}" +
                    $"（已有进度 {state.BaseProgress * 100f:F0}%，同时在操作 {state.Sessions.Count} 人）。");
            }
            else
            {
                // 发包失败：下一帧由玩家重新按 E 即可（客户端可能刚换场景）。
                global::ClueDetail.Core.Log.Warn($"破坏点[{KindName(state)}] #{pid} 进度条挂载失败，稍后重试。");
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

                if (BreakerStates.AnyBroken)
                {
                    // 有人在破坏：另一个点（以及原版电闸，另见 CloseFuseboxWindow）一律锁死。
                    return false;
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
        /// 每个会话一条 100ms 自续链：推进进度、下发刻度、到点完成。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 2026-10-20 起**每个玩家一条链**（原来只有一条，挂在唯一会话上）。
        /// 谁的链先跑到 1.0，谁触发 <see cref="Finish"/>；其余人的链会在下一次自校验时
        /// 发现会话已被 <see cref="Finish"/> 清空，自然断掉。
        /// </para>
        /// <para>
        /// **链的终止完全靠自校验**：会话被移除（<see cref="AbortSession(BreakerState)"/> 或单人版）
        /// / 操作者走了或死了 / 阶段变了 ⇒ 任一不成立就不再排下一次。延迟最多一个节拍（100ms）。
        /// </para>
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
                float startAt;
                if (!state.Sessions.TryGetValue(pid, out startAt))
                {
                    return;                             // 会话结束或已完成 ⇒ 链断
                }

                var r = ServerGameRoom.Instance;
                var p = r != null ? FindPlayer(r, pid) : null;
                if (r == null || r.State != EGameState.Survive || p == null || !CanOperate(p, device, state))
                {
                    AbortSession(state, pid);           // 离熊 / 死了 / 阶段变了 ⇒ 单人收尾（进度保留）
                    return;
                }

                float progress = ProgressOf(state, pid, Now);
                if (progress >= 1f)
                {
                    Finish(device, state, p);
                    return;
                }

                int bar = ToBar(progress);
                int last;
                state.LastBar.TryGetValue(pid, out last);
                if (bar != last)
                {
                    BreakBar.Set(pid, bar);
                    state.LastBar[pid] = bar;
                }

                ScheduleBar(device, state, pid);         // 排下一个
            });
        }

        /// <summary>进度满：切换破坏 / 修复状态。</summary>
        private static void Finish(BreakerDevice device, BreakerState state, ServerGamePlayer player)
        {
            // ⚠️ player 允许为 null：`cd_sabotage` 命令（实验性）要能在没有玩家操作的情况下翻转状态，
            // 那时没有进度条、也没有「谁做的」可记（线索本来就是 11037 = 黑幕）。
            int pid = player != null && player.PublicInfo != null ? player.PublicInfo.PlayerId : 0;
            bool wasBroken = state.Broken;

            if (pid != 0)
            // 收掉**所有**会话：任何人跑满都意味着这一轮结束，
            // 其他人屏幕上的进度条与「在操作」的动作要一起收掉。
            foreach (var pair in state.Sessions)
            {
                BreakBar.End(pair.Key);
                SetBusyState(FindPlayer(pair.Key), false);   // 方案 A：收掉"在操作"的动作
            }

            state.Sessions.Clear();
            state.LastBar.Clear();
            state.BaseProgress = 0f;
            state.Progress = 0f;
            state.Broken = !wasBroken;

            if (state.Broken)
            {
                // 与电闸共用：破坏之后本轮不再可用，要等下一次重派发。
                state.CanBreak = false;

                // 时间污染要冻结的就是「破坏完成的那一刻」，必须在状态翻转处同步记下。
                var tm = ServerTimeManager.Instance;
                state.BrokenAt = tm != null ? tm.SurviveTime : 0;
                state.RepairedAt = 0;         // 新一轮破坏：窗口重新敞开（见 BreakerState.RepairedAt）
            }
            else
            {
                // 修复完成：关上污染窗口。此后新记录（Time >= RepairedAt）不再算污染，
                // 而窗口内的旧记录仍然算 —— 这就是"残留"，线索与镜子共用同一判据。
                var tm2 = ServerTimeManager.Instance;
                state.RepairedAt = tm2 != null ? tm2.SurviveTime : 0;

                // 时钟修好：把破坏期间攒下的回放帧整体打乱后补发。
                if (state.Kind == BreakerKind.Clock)
                {
                    TimelineReplayDelayHook.Flush();
                }
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

            OnStateChanged(device, state, wasBroken, player);
        }

        /// <summary>
        /// 结束当前会话并**保留进度**（离熊 / 换人 / 阶段切换）。
        /// </summary>
        /// <summary>
        /// 结束**某一个**会话（离熊 / 死了 / 阶段变了），把他跑到的进度并进共享进度。
        /// </summary>
        /// <remarks>
        /// 2026-10-20 新增：多会话之后必须能单独收人 —— 一个人走开不应该把
        /// 别人的进度条也撤掉。
        /// </remarks>
        private static void AbortSession(BreakerState state, int pid)
        {
            if (state == null || pid == 0)
            {
                return;
            }

            float startAt;
            if (!state.Sessions.TryGetValue(pid, out startAt))
            {
                return;
            }

            float progress = ProgressOf(state, pid, Now);
            if (progress > state.BaseProgress)
            {
                state.BaseProgress = progress < 1f ? progress : 1f;
            }

            state.Sessions.Remove(pid);
            state.LastBar.Remove(pid);
            BreakBar.End(pid);
            SetBusyState(FindPlayer(pid), false);   // 方案 A：收掉"在操作"的动作
        }

        /// <summary>
        /// 结束**全部**会话（阶段切换 / 换局 / 设备消失 / 命令强制翻转）。
        /// </summary>
        private static void AbortSession(BreakerState state)
        {
            if (state == null)
            {
                return;
            }

            // 先把每个人的进度并进共享进度，再统一收尾。
            foreach (var pair in state.Sessions)
            {
                float progress = ProgressOf(state, pair.Key, Now);
                if (progress > state.BaseProgress)
                {
                    state.BaseProgress = progress < 1f ? progress : 1f;
                }

                BreakBar.End(pair.Key);
                SetBusyState(FindPlayer(pair.Key), false);
            }

            state.Sessions.Clear();
            state.LastBar.Clear();
        }

        /// <summary>算某个会话此刻的进度（0..1）；没有这个会话时返回共享进度。</summary>
        private static float ProgressOf(BreakerState state, int pid, float now)
        {
            if (state == null)
            {
                return 0f;
            }

            float startAt;
            if (!state.Sessions.TryGetValue(pid, out startAt))
            {
                return state.BaseProgress;
            }

            // 破坏与修复用**不同**时长（2026-10-20 拆分）：
            //   state.Broken == false ⇒ 这一步是把「未破坏」推成「已破坏」⇒ 黑方破坏时长
            //   state.Broken == true  ⇒ 这一步是在修复已破坏的点   ⇒ 白方修复时长
            float duration = state.Broken ? WhiteRepairSecondsValue : BlackBreakSecondsValue;
            float p = state.BaseProgress + (now - startAt) / duration;
            return p < 0f ? 0f : (p > 1f ? 1f : p);
        }

        /// <summary>
        /// 方案 A：让**其他人看到**这个人正在操作破坏点。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 服务端 <c>Player.State</c> 的 setter（0.1.18 :178630）会自动
        /// <c>BroadcastModifyPlayer(ChangePlayerState)</c> ⇒ 所有客户端收到 <c>S_MODIFY_PLAYER</c>
        /// （opcode 1023）→ <c>Player.Modify</c> → 客户端 <c>State</c> setter（:12525）→ <c>UpdateAnimation()</c>。
        /// </para>
        /// <para>
        /// ⚠️ <b>必须是 <c>Casting</c>，不是 <c>Interact</c></b>：客户端 <c>UpdateAnimation</c>（:13466）里
        /// <c>Interact</c> 与 Idle/Run/Carry 同分支（播 <c>1_Idle</c>，**没有任何操作动作**），
        /// 只有 <c>Casting</c> 播 <c>8_Interact</c>（循环的"蹲下操作"）。
        /// </para>
        /// <para>
        /// ⚠️ 服务端 <c>StartState/EndState(EPlayerState)</c>（:180116 / :180144）对 <c>Casting</c> **无副作用**
        /// （只有 Idle/Run 刷速度、Hide/Sit 锁技能掉物品），所以可以安全地设。
        /// </para>
        /// <para>
        /// ⚠️ 只在我们设过的那种状态下恢复：玩家原本可能是 Hide/Sit，别把人家拽回 Idle。
        /// 玩家中途移动时客户端会自己把状态改回 <c>Run</c> ⇒ 那正是"中断操作"，与我们的距离判定一致。
        /// </para>
        /// </remarks>
        private static void SetBusyState(ServerGamePlayer player, bool busy)
        {
            if (player == null || player.PublicInfo == null)
            {
                return;
            }

            if (busy)
            {
                if (player.State != EPlayerState.Casting)
                {
                    player.State = EPlayerState.Casting;
                }

                return;
            }

            if (player.State == EPlayerState.Casting)
            {
                player.State = EPlayerState.Idle;
            }
        }

        /// <summary>按座位号找玩家（只用于收掉动作）。</summary>
        private static ServerGamePlayer FindPlayer(int pid)
        {
            var room = ServerGameRoom.Instance;
            if (room == null || room.Players == null || pid == 0)
            {
                return null;
            }

            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                {
                    return p;
                }
            }

            return null;
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

        // ══════════════════ 热开关（2026-10-18） ══════════════════

        /// <summary>
        /// 功能被热开启：把破坏点重新放回场景，并按**电闸的真实状态**决定能不能拆。
        /// </summary>
        /// <remarks>
        /// 为什么不直接调用 <see cref="OnWindowStart"/>：那会把两个点无条件置成「可破坏」，
        /// 等于热开一次就白送一个窗口。这里改成去读电闸的真实状态 ——
        /// 存在 <c>MissionType == -1</c> 的电闸，就说明当前窗口开着（原版 <c>Fusebox.StartMission</c>
        /// 就是这么打的标记，:165671）。
        /// </remarks>
        public static void OnEnabled()
        {
            EnsureDevices();

            bool windowOpen = FuseboxWindowOpen();
            var dm = ServerDeviceManager.Instance;

            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                var state = BreakerStates.All[i];
                state.CanBreak = windowOpen;
                state.Broken = false;
                state.BaseProgress = 0f;
                state.Progress = 0f;
                state.Faked.Clear();
                state.LastResend.Clear();

                var device = FindDevice(dm, state);
                if (device == null)
                {
                    continue;
                }

                SetSlot0(device, 0);
                if (windowOpen)
                {
                    SendPin(state, device, true);
                    RefreshFakes(device, state);
                }
            }

            global::ClueDetail.Core.Log.Info(
                $"破坏点：功能已热开启（电闸窗口{(windowOpen ? "开着" : "关着")}）。");
        }

        /// <summary>
        /// 功能被热关闭：撤掉全部表现，并把两只小熊从场景里移除。
        /// </summary>
        /// <remarks>
        /// 不做这一步的后果：关掉之后场景里留着两只**没有任何交互**的熊，
        /// 而且 <c>StateList[1]</c> 的假主人还留在某些客户端上 —— 他们看得到「按 E」提示，
        /// 按下去却什么都不会发生。
        /// </remarks>
        public static void OnDisabled()
        {
            var dm = ServerDeviceManager.Instance;
            bool windowWasClosed = BreakerStates.AnyBroken;

            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                var state = BreakerStates.All[i];
                var device = FindDevice(dm, state);

                try
                {
                    AbortSession(state);            // 撤进度条
                    AbortFakes(state);              // 收回假主人

                    if (state.ArrowSent)
                    {
                        BroadcastRemoveArrow(device);
                        state.ArrowSent = false;
                    }

                    if (device != null)
                    {
                        SendPin(state, device, false);   // 撤黑幕的图钉
                        dm.RemoveDevice(device);         // 撤掉小熊本体
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(
                        ex, $"破坏点：热关闭时清理[{KindName(state)}]失败（可忽略，状态已重置）。");
                }

                BreakerStates.ResetOne(state);
            }

            BreakBar.ClearAll();

            // 我们可能在上面的破坏里关掉了电闸窗口 —— 热关闭要把这一笔还回去，
            // 否则这一局剩下的时间黑幕连电闸都拆不了（窗口只能靠原版「供电恢复 + 60 秒」重开）。
            if (windowWasClosed)
            {
                try
                {
                    var room = ServerGameRoom.Instance;
                    if (room != null && room.State == EGameState.Survive && dm != null
                        && dm.GetDisconnectFuseCount() == 0)
                    {
                        dm.StartFuseboxSabotage();
                        global::ClueDetail.Core.Log.Info("破坏点：热关闭时把电闸窗口交还给原版（重新派发 3 个电闸）。");
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：热关闭时恢复电闸窗口失败。");
                }
            }

            global::ClueDetail.Core.Log.Info("破坏点：功能已热关闭（破坏点与相关表现已撤除）。");
        }

        /// <summary>
        /// 当前是否处于「可破坏窗口」—— 判据与原版一致：存在正在派发中的电闸。
        /// </summary>
        /// <remarks>
        /// 原版 <c>Fusebox.StartMission</c>（0.1.18 :165671）把 <c>MissionType</c> 置成 -1，
        /// <c>DisconnetCable</c>（:165654）与 <c>ClearSabotage</c>（:165681）把它清回 0。
        /// 所以「有 <c>MissionType == -1</c> 的电闸」就是「窗口开着」的等价判据。
        /// </remarks>
        /// <summary>
        /// 坐标配置被改了就把设备**重建**到新位置（**热更新**）。
        /// </summary>
        /// <remarks>
        /// 为什么必须重建：客户端的 <c>S_MODIFY_DEVICE</c> 处理只刷
        /// Bubble / MissionType / StateList，**不更新 Pos**（0.1.18 :27850 的 <c>Modify</c>），
        /// 所以光改服务端的 <c>DeviceInfo.Pos</c> 不会让那只熊在客户端挪窝。
        /// 每秒检查一次（跟着 <see cref="Tick"/> 的节拍），不一致就移除重建。
        /// </remarks>
        private static void EnsurePosition(ServerDeviceManager dm, BreakerState state, BreakerDevice device)
        {
            if (dm == null || state == null || device == null || device.DeviceInfo == null)
            {
                return;
            }

            var pos = device.DeviceInfo.Pos;
            if (pos == null)
            {
                return;
            }

            float wantX = state.Kind == BreakerKind.Identify ? IdentifyX : ClockX;
            float wantY = state.Kind == BreakerKind.Identify ? IdentifyY : ClockY;

            if (global::System.Math.Abs(pos.X - wantX) < 0.5f && global::System.Math.Abs(pos.Y - wantY) < 0.5f)
            {
                return;
            }

            global::ClueDetail.Core.Log.Info(
                $"破坏点[{KindName(state)}] 坐标已改为 ({wantX:F0},{wantY:F0})，重建设备。");

            try
            {
                AbortSession(state);
                AbortFakes(state);

                if (state.ArrowSent)
                {
                    BroadcastRemoveArrow(device);
                    state.ArrowSent = false;
                }

                SendPin(state, device, false);
                dm.RemoveDevice(device);
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "破坏点：坐标变更时清理旧设备失败。");
            }

            var fresh = EnsureOne(dm, state);
            if (fresh != null)
            {
                SetSlot0(fresh, state.Broken ? 2 : 0);
                BroadcastState(fresh);
                RefreshFakes(fresh, state);
            }
        }

        private static bool FuseboxWindowOpen()
        {
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Fuseboxes == null)
            {
                return false;
            }

            var list = dm.Fuseboxes;
            for (int i = 0; i < list.Count; i++)
            {
                var fusebox = list[i];
                if (fusebox != null && fusebox.DeviceInfo != null && fusebox.DeviceInfo.MissionType == -1)
                {
                    return true;
                }
            }

            return false;
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
        /// <summary>
        /// 破坏 / 修复状态刚翻转时的收口。
        /// </summary>
        /// <remarks>
        /// 分期实现的挂点，集中在这里可以保证「状态已经写好、广播已经发完」之后才动手：
        /// <list type="bullet">
        /// <item>P2：身份 / 时间线索污染的开关（污染是否生效由 <see cref="BreakerState.Broken"/> 与阶段共同判定）；</item>
        /// <item>P3：破坏 +10 秒给所有存活者发断电箭头；修复时撤箭头；撤 / 增电闸图钉；</item>
        /// <item>P4：每次完整破坏往 <c>ClueList</c> 追加一条 <c>{11037, 破坏时刻, 房间}</c>
        /// —— 侦探阶段开始时原版 <c>Device.StartDetective</c> 会自动把 <c>Bubble</c> 置 -1；</item>
        /// <item>P6（2026-10-18 口径）：破坏即封锁其他一切破坏（另一个点 + 原版电闸窗口），
        /// 修复完成后起 <see cref="ReloadSeconds"/> 秒 CD 再重派发。</item>
        /// </list>
        /// </remarks>
        /// <param name="device">破坏点设备。</param>
        /// <param name="state">它的运行期状态（此时 <see cref="BreakerState.Broken"/> 已是翻转后的新值）。</param>
        /// <param name="wasBroken">翻转前是否已破坏。</param>
        /// <param name="player">完成这次破坏 / 修复的玩家（记线索时要给他一条角色脸）。</param>
        private static void OnStateChanged(
            BreakerDevice device, BreakerState state, bool wasBroken, ServerGamePlayer player)
        {
            if (!wasBroken && state.Broken)
            {
                // ① 破坏期间封锁其他一切破坏：另一个点同时进 CD，两个点的图钉一起撤。
                LockAllBreakers();
                SendAllPins(false);

                // ② 与电闸共用同一条窗口：**破坏即关窗**（撤掉 3 个电闸的图钉 / 平板条目 + 禁止再拉闸）。
                CloseFuseboxWindow();

                // ②.5 新一轮破坏开始 ⇒ **上一轮的镜子残留作废**（2026-10-19 用户口径）。
                // 否则身份破坏修好后留下的残留，会在之后**另一次**破坏（如时钟）期间继续轮换脸，
                // 看起来就像"身份破坏明明修好了，镜子还在乱换人"。
                // 注意：残留作废 ≠ 恢复真值 —— 镜子上仍停在最后那张假脸，
                // 直到有人真正再用一次（那时真值变化 ⇒ 客户端自然更新）。
                BreakerStates.MirrorResidual.Clear();

                // ③ 延迟发箭头 —— 刻意不发任何文字通知。
                state.ArrowSent = false;
                ScheduleArrow(state, device);

                // ④ 记一条「黑幕 于 … 在本房间 切断了电力」（每次完整破坏都追加，语义同原版电闸）。
                RecordSabotageClue(device, state, player);

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
                SendAllPins(false);

                // ④.5 2026-10-21（用户口径）：**修复完成的同一刻**就把还没恢复供电的电箱复位。
                // 旧写法把它放在 CD 结束的 ReopenWindow 里 —— 那 60 秒里玩家看到的仍是
                // "灯灭着、闸坏着"，而小熊已经修好了，与"破坏结束"的直觉相反。
                // 现在：修复 ⇒ 电箱立刻恢复 ⇒ CD 只是"多久后能再拆"，两件事分开。
                var dm = ServerDeviceManager.Instance;
                if (dm != null && dm.GetDisconnectFuseCount() > 0)
                {
                    ForceRestoreFuses(dm);
                }

                // ⑤ 破坏结束 ⇒ 起 CD，到点重新派发（电闸与两个点一起恢复可破坏）。
                ScheduleReload();

                global::ClueDetail.Core.Log.Info(
                    $"破坏点[{KindName(state)}] 已修复：{PollutionSummary()}" +
                    $"将于 {ReloadSeconds} 秒后重新派发破坏窗口。");
            }
        }

        /// <summary>把两个破坏点一起置为「不可破坏」（破坏期间封锁其他破坏）。</summary>
        private static void LockAllBreakers()
        {
            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                BreakerStates.All[i].CanBreak = false;
            }
        }

        /// <summary>对两个点各发一次图钉包（<paramref name="isAdd"/> = 加 / 撤），用于封锁与恢复。</summary>
        private static void SendAllPins(bool isAdd)
        {
            var dm = ServerDeviceManager.Instance;
            for (int i = 0; i < BreakerStates.All.Length; i++)
            {
                var st = BreakerStates.All[i];
                var device = FindDevice(dm, st);
                if (device != null)
                {
                    SendPin(st, device, isAdd);
                }
            }
        }

        /// <summary>
        /// 关掉电闸窗口：原版三个电闸的 <c>MissionType</c> 清回 0，并撤掉它们的图钉 / 平板任务条目。
        /// </summary>
        /// <remarks>
        /// 纯调原版 <c>DeviceManager.ClearFuseboxSabotage</c>（0.1.18 :166838）→ 逐个
        /// <c>Fusebox.ClearSabotage</c>（:165677）：<c>MissionType = 0</c> + <c>BroadcastState</c> +
        /// <c>SendSabotageMission(..., isAdd: false)</c>。**图钉与平板条目就是靠最后那一包撤掉的。**
        /// <para>
        /// 副作用（已接受，2026-10-18 口径）：原版「拆掉 1 个电箱、另外 2 个仍可拆」的节奏被打断 ——
        /// 只要我们的破坏点进入破坏态，电闸这一轮就整体作废，直到 CD 到点重派发。
        /// </para>
        /// </remarks>
        private static void CloseFuseboxWindow()
        {
            var dm = ServerDeviceManager.Instance;
            if (dm == null)
            {
                return;
            }

            try
            {
                dm.ClearFuseboxSabotage();
                global::ClueDetail.Core.Log.Info("破坏点：已关闭电闸窗口（撤掉电闸图钉 / 平板条目，封锁其他破坏）。");
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "破坏点：关闭电闸窗口失败。");
            }
        }

        /// <summary>
        /// 破坏结束后起 <see cref="ReloadSeconds"/> 秒 CD，到点重新派发破坏窗口。
        /// </summary>
        /// <remarks>
        /// 为什么必须自建计时器：原版那条 60 秒只由「供电全部恢复」触发
        /// （<c>AreaManager.RefreshLight</c> 的 case 0，:176585），而我们的破坏**不动光照** ⇒ 永远等不到那个信号。
        /// <para>
        /// ⚠ <c>TimeManager.PushSurvivalJob</c> 的回调**不随阶段清空**，所以 <see cref="ReopenWindow"/> 第一句要自校验。
        /// </para>
        /// </remarks>
        private static void ScheduleReload()
        {
            var tm = ServerTimeManager.Instance;
            if (tm == null)
            {
                ReopenWindow();
                return;
            }

            tm.PushSurvivalJob(ReloadSeconds, delegate
            {
                try
                {
                    ReopenWindow();
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：CD 到点重新派发失败。");
                }
            });
        }

        /// <summary>
        /// CD 到点：把破坏窗口交还给原版重派发（<c>StartFuseboxSabotage</c>）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 三种情况**不**重派发：功能被热关掉、阶段已不是 Survive、另一个点仍在破坏中。
        /// </para>
        /// <para>
        /// ⚠️ <b>2026-10-21 修（用户实机报告的死锁）</b>：旧写法在
        /// <c>GetDisconnectFuseCount() &gt; 0</c> 时直接 <c>return</c>，理由是"交给原版供电恢复流程"。
        /// 但原版那条链只在**供电全部恢复**时触发（<c>AreaManager.RefreshLight</c> 的
        /// <c>case 0</c>，:176585），而单台电闸坏着时：① <c>RefreshLight</c> 走 <c>case 1</c>、
        /// 不置 <c>Area.IsLight = false</c>；② 客户端 <c>Fusebox.GetInteractMessage</c> 要求
        /// <c>Managers.Game.Darkness</c>（:3244），而 <c>Darkness = !Area.IsLight</c>（:30610）；
        /// ③ 服务端 <c>ConnetCable</c> 的守卫同样是 <c>!Area.IsLight</c>（:165624）。
        /// ⇒ **这台电闸客户端不给修复提示、服务端也拒绝修复**，供电永远恢复不了，
        /// 我们的两个破坏点就永久锁死。
        /// </para>
        /// <para>
        /// 现在分两处收口：
        /// ① **修复完成的那一刻**（<c>OnStateChanged</c> 的 ④.5）就把未恢复的电箱复位 ——
        ///    玩家立刻看到灯亮、闸好；"破坏结束"与"CD 倒计时"是分开的两件事；
        /// ② 这里再查一次纯属兜底：CD 期间若又有人拉断一台，到点时一并复位。
        /// 这些电闸确实是黑幕拆的，但**原版对「只拆一台」没有自愈路径**（单台坏闸连修复提示
        /// 都不给，见上面的机制链）—— 不处理它们，下一次派发就会被「供电未恢复」卡住。
        /// </para>
        /// </remarks>
        private static void ReopenWindow()
        {
            if (!global::ClueDetail.Core.FeatureLoader.Enabled<ShadowSabotageFeature>())
            {
                return;
            }

            var room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                return;
            }

            if (BreakerStates.AnyBroken)
            {
                return;
            }

            var dm = ServerDeviceManager.Instance;
            if (dm == null)
            {
                return;
            }

            if (dm.GetDisconnectFuseCount() > 0)
            {
                // 兜底：正常情况下修复那一刻已经复位过了（OnStateChanged 的 ④.5）。
                // 还能走到这里只有一种可能 —— CD 期间又有电箱被拉断。
                ForceRestoreFuses(dm);
            }

            dm.StartFuseboxSabotage();
            global::ClueDetail.Core.Log.Info($"破坏点：CD（{ReloadSeconds} 秒）到点，重新派发破坏窗口。");
        }

        /// <summary>
        /// 把场上所有**未恢复供电**的电闸强制复位成"已修好"。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么不能直接调 <c>dm.RefuseAllFuse()</c>：它逐个 <c>ConnetCable()</c>，
        /// 而 <c>ConnetCable</c> 的守卫是 <c>!GetArea(Pos).IsLight</c>。
        /// 单台电闸坏着时 <c>RefreshLight</c> 走 <c>case 1</c>、区域仍是亮的 ⇒ 守卫不成立 ⇒
        /// 什么都不做。所以这里先把该区域 <c>IsLight</c> 置 false 打开守卫，
        /// 再让 <c>ConnetCable</c> 走它自己的完整收口（IsLight=true / 撤箭头 / StateList[0]=0 /
        /// InitProgress / 广播）。区域明暗紧接着会被它自己的 <c>RefreshLight()</c> 重算，不会残留。
        /// </para>
        /// <para>
        /// ⚠️ 最后一台恢复时，<c>RefreshLight</c> 的 <c>case 0</c> 会自己排一次
        /// 60 秒后的 <c>StartFuseboxSabotage</c>；与紧接着的那次派发重复，
        /// 但 <c>StartFuseboxSabotage</c> 开头就是 <c>ClearFuseboxSabotage()</c>，幂等。
        /// </para>
        /// </remarks>
        private static void ForceRestoreFuses(ServerDeviceManager dm)
        {
            var am = Server.Game.AreaManager.Instance;
            if (dm == null || dm.Fuseboxes == null || am == null)
            {
                return;
            }

            int restored = 0;
            for (int i = 0; i < dm.Fuseboxes.Count; i++)
            {
                var fuse = dm.Fuseboxes[i];
                if (fuse == null || fuse.DeviceInfo == null || fuse.DeviceInfo.Pos == null || fuse.IsLight)
                {
                    continue;
                }

                try
                {
                    var area = am.GetArea(fuse.DeviceInfo.Pos);
                    if (area != null)
                    {
                        area.IsLight = false;      // 打开 ConnetCable 的守卫
                    }

                    fuse.ConnetCable();
                    restored++;
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Warn(
                        $"破坏点：强制恢复电箱 {fuse.ID} 失败（{ex.Message}），跳过后继续。");
                }
            }

            if (restored > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"破坏点：本次破坏期间有 {restored} 台电箱未恢复供电，已全部强制复位" +
                    "（原版对「只拆一台」没有自愈路径，留着会让下一次派发一直卡在「供电未恢复」上）。");
            }
        }

        /// <summary>
        /// 破坏后按两档发箭头：黑方 / 黑幕用 <see cref="ShadowSabotageFeature.NotifyBlackDelayValue"/>，
        /// 白方用 <see cref="ShadowSabotageFeature.NotifyWhiteDelayValue"/>。
        /// </summary>
        /// <remarks>
        /// ⚠ <c>TimeManager.PushSurvivalJob</c> 的回调**不随阶段清空**，
        /// 所以回调第一句必须自校验（见 <see cref="SendArrow"/>：<c>Broken</c> 与阶段都要查）。
        /// </remarks>
        private static void ScheduleArrow(BreakerState state, BreakerDevice device)
        {
            // 2026-10-20：分两档 —— 黑方 / 黑幕立即（NotifyBlackDelay），白方延迟（NotifyWhiteDelay）。
            PushArrow(state, device, (int)global::System.Math.Round(NotifyBlackDelayValue), true);
            PushArrow(state, device, (int)global::System.Math.Round(NotifyWhiteDelayValue), false);
        }

        /// <summary>把「某一档的箭头」排进队列；delay &lt;= 0 时立即发。</summary>
        private static void PushArrow(BreakerState state, BreakerDevice device, int delay, bool toDark)
        {
            if (delay <= 0)
            {
                SendArrow(state, device, toDark);
                return;
            }

            var tm = ServerTimeManager.Instance;
            if (tm == null)
            {
                SendArrow(state, device, toDark);
                return;
            }

            tm.PushSurvivalJob(delay, delegate
            {
                try
                {
                    SendArrow(state, device, toDark);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(
                        ex, $"破坏点：延迟发送{(toDark ? "黑方" : "白方")}箭头失败。");
                }
            });
        }

        /// <summary>发箭头（带自校验：已修复 / 已发过 / 已离开 Survive 都不发）。</summary>
        private static void SendArrow(BreakerState state, BreakerDevice device, bool toDark)
        {
            // ⚠️ 不再用 ArrowSent 做「只发一次」的闸：现在是两档各发一次。
            // ArrowSent 的含义变成「至少有一档的箭头已经发出去过」，离开 Survive 时据此撤箭头。
            if (state == null || !state.Broken)
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

            BroadcastArrow(target, toDark);
            state.ArrowSent = true;

            float used = toDark ? NotifyBlackDelayValue : NotifyWhiteDelayValue;
            global::ClueDetail.Core.Log.Info(
                $"破坏点[{KindName(state)}] 已给{(toDark ? "黑方 / 黑幕" : "白方")}发出断电箭头" +
                $"（延迟 {used:F0} 秒）。");
        }

        /// <summary>
        /// 往破坏点的 <c>ClueList</c> 记一条「黑幕 于 … 在本房间 切断了电力」。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>口径（2026-10-18）：每次完整破坏都记一条，语义与原版电闸完全对齐。</b>
        /// 原版电闸走 <c>Device.RecordLastUsingPlayer(player, EWhatState.Mastermind)</c>（0.1.18 :166386）：
        /// <c>PlayerId = 11037</c>、<c>Time = 当刻 SurviveTime</c>、<c>RoomId = 本设备房间</c>，
        /// 并按 **30 秒窗口合并**（:166404-166437：同窗口内已有同 <c>PlayerId</c> 的未销毁条目 ⇒
        /// 先 <c>S_REMOVE_CLUE</c> 再 <c>S_ADD_CLUE</c>、只更新时间；跨窗口则追加新条目）。
        /// 所以同一局破坏两次（间隔超过 30 秒）会留下**两条**线索，卡片上是两行。
        /// </para>
        /// <para>
        /// <b>为什么不直接调原版方法：</b>它要求 <c>DeviceData.WhatType != EWhatType.WNone</c>（:166388），
        /// 而我们的破坏点刻意保持 <c>WNone</c>（不参与原版线索 / 销毁证据系统，避免多出第二种交互）。
        /// 这里把同一段语义**复刻**一遍，产出与原版一致。
        /// </para>
        /// <para>
        /// <b>刻意不设 <c>DeviceInfo.Bubble = -1</c>：</b>客户端 <c>DeviceBase.InteractBase</c>（:4843）与
        /// <c>GetInteractMessageBase</c>（:4880）都把 <c>Bubble == -1</c> 放在第一优先 —— 一旦置上，
        /// 按 E 就变成「调查」而**再也不能修复**，所以 Survive 期间必须保持 <c>Bubble == 0</c>。
        /// 也不需要自己设：原版 <c>Device.StartDetective</c>（:166531）在侦探阶段会做
        /// <c>ClueList.Count == 0 ? Bubble = 0 : Bubble = -1</c> 并广播 ⇒ 只要这里有线索就自动可调查。
        /// </para>
        /// <para>
        /// 跨局由原版负责：<c>Device.StartSurvival</c>（:166516）会 <c>ClueList.Clear()</c> 并发 <c>S_CLEAR_CLUES</c>。
        /// </para>
        /// </remarks>
        private static void RecordSabotageClue(BreakerDevice device, BreakerState state, ServerGamePlayer player)
        {
            var room = ServerGameRoom.Instance;
            var tm = ServerTimeManager.Instance;
            if (device == null || device.ClueList == null || room == null || tm == null)
            {
                return;
            }

            try
            {
                int now = tm.SurviveTime;
                int limitTime = global::ClueDetail.Core.ClueMergeWindow.LimitTime(now);  // 桶长可配置（默认 = 原版 30 秒口径，:166404）

                PropositionInfo exist = null;
                var list = device.ClueList;
                for (int i = 0; i < list.Count; i++)
                {
                    var x = list[i];
                    if (x != null && !x.Destroyed && x.Time >= limitTime
                        && x.PlayerId == Define.MASTERMIND_SABOTAGE_ID)
                    {
                        exist = x;
                        break;
                    }
                }

                if (exist == null)
                {
                    var fresh = new PropositionInfo
                    {
                        PlayerId = Define.MASTERMIND_SABOTAGE_ID,   // 11037 = 黑幕 + 切断电力
                        Time = now,
                        RoomId = device.RoomID,
                    };

                    list.Add(fresh);
                    room.Broadcast(new S_ADD_CLUE { DeviceId = device.ID, Info = fresh.Clone() });

                    global::ClueDetail.Core.Log.Info(
                        $"破坏点[{KindName(state)}] 已追加可调查线索" +
                        $"（黑幕 / {now} 秒 / 房间 {device.RoomID}）。");
                }
                else
                {
                    // 同窗口内合并：先撤旧副本、再发新副本（与原版 :166423-166436 一致）。
                    var old = exist.Clone();
                    list.Remove(exist);
                    exist.Time = now;
                    list.Add(exist);

                    room.Broadcast(new S_REMOVE_CLUE { DeviceId = device.ID, Info = old });
                    room.Broadcast(new S_ADD_CLUE { DeviceId = device.ID, Info = exist.Clone() });

                    global::ClueDetail.Core.Log.Info(
                        $"破坏点[{KindName(state)}] 30 秒窗口内已有同名线索，已把它的时间更新为 {now} 秒。");
                }

                // 与原版电闸一致：给操作者本人补一条「角色脸」，告诉他这事是黑幕干的。
                if (player != null && player.Session != null)
                {
                    player.Session.Send(new S_LEFT_CLUE { RoleFace = true });
                }
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "破坏点：记录可调查线索失败。");
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
        /// 时间污染把破坏期间写下的新线索**额外打上**「显示时刻 = 它」的未知字段（101），
        /// 真值里的 <c>Time</c> 不动（去重窗口要靠它）—— 见 <c>Patch.DisplayTimeStampHook</c>。
        /// </summary>
        internal static int FrozenTime
        {
            get { return BreakerStates.Clock.BrokenAt; }
        }

        /// <summary>
        /// **统一判据**：某个记录时刻是否落在某次破坏的「污染窗口」内。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 污染窗口 = <c>[BrokenAt, RepairedAt)</c>，<c>RepairedAt == 0</c> 表示还没修复（窗口向右敞开）。
        /// 这是全模块判断"某条记录是否产生于污染期间"的**唯一**依据。
        /// </para>
        /// <para>
        /// ⚠️ <b>刻意不看阶段、不看 <c>Broken</c></b>：
        /// <list type="bullet">
        /// <item>不看阶段 —— 线索列表主要在**调查阶段**才推送，镜子熄灭又发生在**刚离开 Survive**
        /// 的那一刻；用 <c>PollutionOn</c>（内含 <c>State == Survive</c>）判断会恒为 false
        /// （2026-10-20 实际踩过：进调查时镜子一台都没熄灭、所有人看到真实时间）；</item>
        /// <item>不看 <c>Broken</c> —— 修复后窗口内的旧记录仍然算污染，这正是"残留"语义
        /// （用户口径：污染结束不恢复，直到其恢复正常）。</item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <param name="recordTime">记录时刻（<c>TimeManager.SurviveTime</c> 秒）。</param>
        /// <param name="byIdentity">是否落在身份破坏的窗口内。</param>
        /// <param name="byClock">是否落在时钟破坏的窗口内。</param>
        internal static bool IsPollutedRecord(int recordTime, out bool byIdentity, out bool byClock)
        {
            byIdentity = InWindow(BreakerStates.Identify, recordTime);
            byClock = InWindow(BreakerStates.Clock, recordTime);
            return byIdentity || byClock;
        }

        /// <summary>简化版：只要知道是否被污染。</summary>
        internal static bool IsPollutedRecord(int recordTime)
        {
            return IsPollutedRecord(recordTime, out _, out _);
        }

        private static bool InWindow(BreakerState state, int time)
        {
            if (state == null || state.BrokenAt <= 0 || time < state.BrokenAt)
            {
                return false;                  // 没破坏过，或记录早于破坏
            }

            return state.RepairedAt <= 0 || time < state.RepairedAt;
        }

        /// <summary>
        /// **房主端**的"虚拟污染"：按当前污染口径把一条真值线索改写成它应该显示的样子。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么需要它（2026-10-19 用户实测「房主端均为真实线索，忽视线索破坏机制」）：
        /// 虚弱（污染）的实现是**出站改写副本**（<c>HostPeerSession.Send</c> 的 Prefix），
        /// 它只作用于**发给客户端的包**。而房主自己的客户端渲染线索时读的是服务端真值
        /// <c>deviceById.ClueList</c>（客户端 <c>DeviceBase</c> :5031 的房主分支），
        /// 完全绕过了出站改写 ⇒ 房主看到的名字、头像、时间**全是真实的**。
        /// </para>
        /// <para>
        /// 头像没有独立字段：客户端 <c>UI_ClueSubItem.FillText</c>（:86640-86667）的
        /// 文本（<c>GetWhoText(Info.PlayerId)</c>）与头像（<c>Info.PlayerId == 0</c> ⇒
        /// <c>Someone_SD</c>）都取自**同一个** <c>Info.PlayerId</c>，所以把 <c>PlayerId</c> 置 0
        /// 会让两者一起变成「某人」—— 不需要（也没有）单独的头像开关。
        /// </para>
        /// <para>
        /// ⚠️ 只在**房主端**调用：非房主已经通过出站包改写拿到污染后的副本，
        /// 再改一遍是重复劳动（而且它的 <c>PlayerId</c> 已经是 0，会被下面的判据挡掉）。
        /// </para>
        /// </remarks>
        /// <param name="clue">要渲染的真值线索。</param>
        /// <param name="polluted">改写成副本（调用方应传回渲染路径）；未污染时为原对象。</param>
        /// <returns>是否发生了改写。</returns>
        internal static bool TryVirtualPollute(ClueManager.Clue clue, out ClueManager.Clue polluted)
        {
            polluted = clue;
            if (clue == null || clue.Info == null)
            {
                return false;
            }

            if (!IdentifyPollutionOn && !TimePollutionOn)
            {
                return false;
            }

            PropositionInfo info = clue.Info;
            int frozen = FrozenTime;
            int idStart = BreakerStates.Identify.BrokenAt;
            int clockStart = BreakerStates.Clock.BrokenAt;

            bool polluteIdentity = IdentifyPollutionOn && idStart > 0 && info.Time >= idStart;
            bool polluteTime = TimePollutionOn && clockStart > 0 && info.Time >= clockStart;
            if (!polluteIdentity && !polluteTime)
            {
                return false;
            }

            // 已经是污染后的（非房主端重复调用）⇒ 不必再改。
            if (polluteIdentity && info.PlayerId == 0)
            {
                return false;
            }

            PropositionInfo copy = info.Clone();
            if (polluteTime)
            {
                copy.Time = frozen;
            }

            if (polluteIdentity)
            {
                copy.PlayerId = 0;
            }

            polluted = new ClueManager.Clue
            {
                Info = copy,
                OwnerId = clue.OwnerId,
                IsStatement = clue.IsStatement,
            };
            return true;
        }
    }
}