using Google.Protobuf;
using Google.Protobuf.Collections;
using Protocol;
using ServerDevice = Server.Game.Device;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGameRoom = Server.Game.GameRoom;
using ServerGamePlayer = Server.Game.Player;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 破坏点用到的**包构造**：全部集中在这里，编排逻辑只调方法、不碰协议类型。
    /// </summary>
    internal partial class ShadowSabotageFeature
    {
        /// <summary>
        /// 把破坏点伪装成「这个玩家自己的小熊」—— 也就是把 <c>StateList[1]</c> 单发成他的 PlayerId。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么能成：客户端 <c>Summon.GetInteractMessage</c>（0.1.18 :4159）只认
        /// <c>StateList[1] == Managers.Player.MyPlayer.PublicInfo.PlayerId</c>，
        /// 成立就返回词条 <c>SummonInteract</c>（按 E 提示），按下去发 <c>C_INTERACT_SUMMON</c>。
        /// 服务端那条链路（<c>DeviceManager.Interact</c> → <c>Device.Interact</c>）只看设备 ID，
        /// **不校验主人**，所以假主人是纯客户端观感、服务端完全可控。
        /// </para>
        /// <para>
        /// 为什么必须**单发**而不是改 <c>DeviceInfo.StateList[1]</c> 后 <c>BroadcastState()</c>：
        /// 那是全场广播，会让所有人都变成「主人」（人人屏幕上一只属于自己的熊）。
        /// </para>
        /// <para>
        /// ⚠ 已接受的副作用：客户端 <c>Summon.Update</c> 每 0.2 秒做一次近身检测
        /// （<c>RuneSfx</c> + HUD 描边，:4027），判据就是 <c>IsOwner</c> —— 假主人无法规避它。
        /// 所以只在**近距离**才发，把窗口压到「他人就在旁边」。
        /// </para>
        /// </remarks>
        internal static void SendFakeOwner(BreakerDevice device, ServerGamePlayer player)
        {
            if (player == null || player.PublicInfo == null)
            {
                return;
            }

            SendOwnerSlot(device, player, player.PublicInfo.PlayerId);
        }

        /// <summary>把「假主人」收回：把 <c>StateList[1]</c> 单发成 0（= 没有主人）。</summary>
        /// <remarks>
        /// 0 是安全值：客户端 <c>IsOwner</c> 要求 <c>StateList[1] == MyPlayerID</c>，
        /// 而玩家 id 从 1 起（<c>PlayerId</c> 是座位号），0 永不相等。
        /// </remarks>
        internal static void SendTruthOwner(BreakerDevice device, ServerGamePlayer player)
        {
            SendOwnerSlot(device, player, 0);
        }

        private static void SendOwnerSlot(BreakerDevice device, ServerGamePlayer player, int ownerValue)
        {
            if (device == null || player == null || player.Session == null)
            {
                return;
            }

            try
            {
                // 克隆：绝不能把临时的假主人写回服务端权威的 DeviceInfo ——
                // 那会被下一次 BroadcastState() 带出去（而且可能被别人读到）。
                var info = device.DeviceInfo.Clone();
                if (info.StateList.Count < 2)
                {
                    return;
                }

                info.StateList[1] = ownerValue;
                player.Session.Send(new S_MODIFY_DEVICE { Info = info });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：伪装主人下发失败（{ex.Message}）。");
            }
        }

        /// <summary>
        /// 把破坏点的**真状态**（<c>StateList[0]</c>）广播给全场。
        /// </summary>
        /// <remarks>
        /// ⚠ 广播的是服务端权威的 <c>DeviceInfo</c>，其中 <c>StateList[1]</c> 恒为 0
        /// ⇒ **会把各客户端上的假主人一起冲掉**。所以调用方之后必须让伪装名单失效、立刻重发
        /// （<see cref="RefreshFakes"/> 做这件事）。
        /// </remarks>
        internal static void BroadcastState(BreakerDevice device)
        {
            if (device == null)
            {
                return;
            }

            try
            {
                device.BroadcastState();
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：状态广播失败（{ex.Message}）。");
            }
        }

        // ══════════════════ 镜子：污染期间的「显示」改写（只改副本，真值不动） ══════════════════

        /// <summary>
        /// 污染期间改写镜子的**显示**：身份破坏 → 脸每秒轮换；时间破坏 → 时间显示冻结值。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>判定口径（2026-10-19 用户两次定案）</b>：
        /// </para>
        /// <list type="number">
        /// <item>**当且仅当镜子本来就要显示某人**（真值 <c>StateList[1] != 0</c>）才处理；</item>
        /// <item>**该记录必须发生在对应破坏期间**（<c>StateList[2] &gt;= BrokenAt</c>）—— 破坏前记下、
        /// 破坏期间没被更新的镜子**一个字都不改**（"不用管"）；</item>
        /// <item>两条污染**各按自己的破坏起点**判定，互不借用时间窗口；</item>
        /// <item>**污染结束后不主动恢复**：残留的假脸留在客户端上，直到有人再次使用镜子
        /// （真值变化 ⇒ 原版自己广播真值 ⇒ 自然恢复正常）。</item>
        /// </list>
        /// <para>
        /// 机制依据：客户端 <c>Mirror.RefreshState</c>（0.1.18 :7522-7548）：
        /// <c>StateList[1] == 0</c> ⇒ 整块镜子关掉；否则 <c>GetPlayerCache(StateList[1])</c> 取立绘、
        /// <c>StateList[2]</c> 取时间。原版只在有人用时写真实值（服务端 :168891/:168892）。
        /// </para>
        /// <para>
        /// 轮换：名单 = 存活、非旁观、非幽灵的玩家（按座位号升序）；按收件人偏移取下标，
        /// 并**避开"照出自己"**（那看起来像没坏）。
        /// </para>
        /// <para>
        /// ⚠️ 只发 <c>Clone()</c> 副本：真值绝不能改。⚠️ 污染结束**不做恢复**（用户口径）；
        /// 只有在**离开 Survive**（= 玩家再也无法通过用镜子把记录刷新成真值）时，
        /// 才由 <see cref="QuenchImpossibleMirrors"/> 把残留镜子**熄灭**，避免假信息带进调查阶段。
        /// </para>
        /// </remarks>
        internal static void CycleMirrors()
        {
            var room = ServerGameRoom.Instance;
            var dm = ServerDeviceManager.Instance;
            if (room == null || dm == null || room.Players == null)
            {
                return;
            }

            // ⚠️ 2026-10-19 修（用户实测）：**轮换的停止条件不能是"污染是否开启"**。
            // 身份破坏修好之后，镜子上的记录仍是那次污染的残留（真值一直没变、客户端也没收到真值）
            // ⇒ 必须**继续轮换**，直到有人真正再用一次镜子把记录刷新成真值。
            // 判据因此改成「这面镜子的当前记录是否仍是污染残留」（见 BreakerStates.MirrorResidual）。
            float now = Now;

            var devices = dm.Objects;
            if (devices == null)
            {
                BreakerStates.MirrorResidual.Clear();
                return;
            }

            // ① 先做残留清理：记录时间变了（被真实使用覆盖）或镜子被熄灭 ⇒ 不再是残留。
            if (BreakerStates.MirrorResidual.Count > 0)
            {
                var stale = new global::System.Collections.Generic.List<int>();
                foreach (var pair in BreakerStates.MirrorResidual)
                {
                    ServerDevice dev = FindMirror(dm, pair.Key);
                    var lst = dev == null || dev.DeviceInfo == null ? null : dev.DeviceInfo.StateList;
                    if (lst == null || lst.Count < 3 || lst[1] == 0 || lst[2] != pair.Value)
                    {
                        stale.Add(pair.Key);
                    }
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    BreakerStates.MirrorResidual.Remove(stale[i]);
                }
            }

            // ⚠️ 2026-10-20 重构：判据统一为「污染窗口」（IsPollutedRecord），
            // **不再用 PollutionOn**（它内含 State == Survive，离开 Survive 后恒为 false）。
            // 这样修复后窗口内的旧记录仍是"残留"（继续轮换/冻结），直到有人重新用镜子刷新。
            if (BreakerStates.MirrorResidual.Count == 0
                && BreakerStates.Identify.BrokenAt <= 0
                && BreakerStates.Clock.BrokenAt <= 0)
            {
                return;      // 这一局从没破坏过 ⇒ 没什么可做
            }

            if (now - BreakerStates.LastMirrorCycle < 1f)
            {
                return;
            }

            BreakerStates.LastMirrorCycle = now;

            // 轮换名单：存活、非旁观、非幽灵的玩家（这面镜子的记录者另外补进去）。
            var ids = new global::System.Collections.Generic.List<int>();
            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.IsAlive && !p.IsSpectator && !p.PublicInfo.IsGhost)
                {
                    ids.Add(p.PublicInfo.PlayerId);
                }
            }

            int tick = (int)now;
            int frozen = FrozenTime;

            for (int i = 0; i < devices.Count; i++)
            {
                ServerDevice device = devices[i];
                if (!(device is global::Server.Game.Mirror) || device.DeviceInfo == null)
                {
                    continue;
                }

                var st = device.DeviceInfo.StateList;
                if (st == null || st.Count < 3 || st[1] == 0)
                {
                    continue;          // 本来就该显示"没人" ⇒ 不用管
                }

                int realFace = st[1];
                int realTime = st[2];

                // 判据（2026-10-22 再确认，用户口径）：**只看镜子记录的时刻是否落在污染窗口内**。
                //   · `realTime` = 镜子的 `StateList[2]`，即"这条记录发生在什么时候"；
                //   · 落在 `[BrokenAt, RepairedAt)` 内 ⇒ 记录是破坏期间留下的；
                //   · 之后镜子没被再次使用（`StateList[2]` 没变）⇒ 一直算残留，继续轮换 / 冻结，
                //     不因为"破坏已修复"而停 —— 这就是"维持破坏时的状态"。
                // ⚠️ 不能改成"当前是否处于破坏态"：那会把破坏**之前**记录的镜子也算进来，
                //    与"只有记录发生在破坏时"的口径相反。破坏态只通过窗口起点 BrokenAt 体现。
                bool duringIdentity = IsPollutedRecord(realTime, out bool byIdentity, out bool byClock);
                duringIdentity = byIdentity;
                bool duringClock = byClock;

                // ⚠️ 2026-10-19 修（用户实测）：**只有身份破坏才进残留表**。
                // 早前写成 `duringIdentity || duringClock`，导致时钟破坏期间被用过的镜子
                // 也被标成"身份残留" ⇒ 脸莫名开始轮换，且刷新后又被写回、永远停不下来。
                // 时钟污染只负责**冻结时间**，与脸的轮换完全无关。
                if (duringIdentity)
                {
                    BreakerStates.MirrorResidual[device.ID] = realTime;
                }

                bool residual = BreakerStates.MirrorResidual.ContainsKey(device.ID);
                if (!residual && !duringClock)
                {
                    continue;          // 既不是身份残留，也没有时间要冻结 ⇒ 不动
                }

                // 名单 = 存活玩家 ∪ 真值记录者（他可能已死亡/旁观，但镜子上确实是他）。
                // ⚠️ 这正是 2026-10-19 修掉的 bug：之前"按收件人偏移 + 跳过自己"会把
                //    收件人自己的脸永远排除，而镜子记下的往往正是"正在看镜子的这个人"。
                var pool = new global::System.Collections.Generic.List<int>(ids);
                if (realFace != 0 && !pool.Contains(realFace))
                {
                    pool.Add(realFace);
                }

                pool.Sort();
                if (pool.Count == 0)
                {
                    continue;
                }

                int face = residual ? pool[tick % pool.Count] : realFace;   // 全场统一、每秒前进一格
                int time = duringClock ? frozen : realTime;

                for (int j = 0; j < room.Players.Count; j++)
                {
                    var p = room.Players[j];
                    if (p == null || p.PublicInfo == null || p.Session == null)
                    {
                        continue;
                    }

                    // ⚠️ 2026-10-19 修：轮换期间**不能**用"与本机真值一致就不发"来跳过。
                    // 轮转到"记录者本人"那一秒，face == realFace，若此时跳过，客户端就收不到
                    // 这次更新、镜子上永远停在上一个人 ⇒ 记录者自己的脸**永远不出现**
                    //（用户实测："依旧不会轮换触发镜子的人自己"）。
                    // 只有"单纯冻结时间、不轮换"时，才按差异节流。
                    if (!residual && face == realFace && time == realTime)
                    {
                        continue;
                    }

                    SendMirrorState(device, p, face, time);
                }
            }
        }

        /// <summary>按设备 id 找一台服务端设备（只用于镜子）。</summary>
        private static ServerDevice FindMirror(ServerDeviceManager dm, int deviceId)
        {
            var devices = dm == null ? null : dm.Objects;
            if (devices == null)
            {
                return null;
            }

            for (int i = 0; i < devices.Count; i++)
            {
                ServerDevice d = devices[i];
                if (d != null && d.ID == deviceId)
                {
                    return d;
                }
            }

            return null;
        }
        /// <summary>
        /// 离开 Survive：把**仍残留着污染脸**的镜子熄灭。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么是这里：调查阶段玩家**无法再使用镜子**（客户端 <c>Mirror.StartContact</c> 只在
        /// <c>State == Survive</c> 时发 <c>C_INTERACT_MIRROR</c>，服务端 <c>Mirror.Interact</c> 也要求
        /// <c>State == Survive</c>）⇒ 残留的假脸**再也没有机会被真实记录覆盖**，
        /// 只能一路带到调查阶段误导侦探。所以离开 Survive 这一刻（"不能再调整"的边界）把它熄灭。
        /// </para>
        /// <para>
        /// 熄灭 = 发 <c>StateList[1] = 0 / [2] = 0</c> 的副本（客户端据此走"镜子关掉"分支：
        /// 换 <c>mirror_body_off</c>、清立绘与时间）。**真值仍然不动**（调查阶段被扫描时仍是真实记录）。
        /// </para>
        /// </remarks>
        internal static void QuenchImpossibleMirrors()
        {
            var dm = ServerDeviceManager.Instance;
            var room = ServerGameRoom.Instance;
            if (dm == null || room == null || room.Players == null)
            {
                return;
            }

            var devices = dm.Objects;
            if (devices == null)
            {
                return;
            }

            int quenched = 0;
            int retimed = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                ServerDevice device = devices[i];
                if (!(device is global::Server.Game.Mirror) || device.DeviceInfo == null)
                {
                    continue;
                }

                var st = device.DeviceInfo.StateList;
                if (st == null || st.Count < 3 || st[1] == 0)
                {
                    continue;
                }

                // ⚠️ 2026-10-20 二次改：判据换成**残留表**。
                // BreakerStates.MirrorResidual 才是「多段污染历史」的累积 ——
                // 破坏期间被用过的镜子都记在里面，修好后**不清**（用户口径：污染结束不恢复）。
                // 原来用 IsPollutedRecord(realTime) 反推：单窗口模型下第二次破坏会把
                // BrokenAt 覆盖掉，第一次污染期用过的镜子就永远判不出来。
                bool residual = BreakerStates.MirrorResidual.ContainsKey(device.ID);
                if (!residual)
                {
                    // 纯时间污染（不是身份残留）：进调查时保持破坏发生时刻，
                    // 不再改成"调查开始这一刻"（用户口径：与破坏时间一致）。
                    if (IsPollutedRecord(st[2], out _, out bool byClock) && byClock)
                    {
                        int frozen = BreakerStates.Clock.BrokenAt;

                        for (int j = 0; j < room.Players.Count; j++)
                        {
                            var q = room.Players[j];
                            if (q == null || q.PublicInfo == null || q.Session == null)
                            {
                                continue;
                            }

                            SendMirrorState(device, q, st[1], frozen);
                        }

                        retimed++;
                    }

                    continue;
                }

                for (int j = 0; j < room.Players.Count; j++)
                {
                    var p = room.Players[j];
                    if (p == null || p.PublicInfo == null || p.Session == null)
                    {
                        continue;
                    }

                    SendMirrorState(device, p, 0, 0);
                }

                quenched++;
            }

            if (quenched > 0 || retimed > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"破坏点：调查阶段开始 —— 熄灭 {quenched} 面身份残留镜子，重设 {retimed} 面时间污染镜子的显示时间。");
            }
        }

        /// <summary>把某台镜子的「脸 + 时间」单发给一个玩家（副本，真值不动）。</summary>
        private static void SendMirrorState(ServerDevice device, ServerGamePlayer player, int faceId, int time)
        {
            try
            {
                DeviceInfo info = device.DeviceInfo.Clone();
                info.StateList[1] = faceId;
                info.StateList[2] = time;
                player.Session.Send(new S_MODIFY_DEVICE { Info = info });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：镜子显示改写下发失败（{ex.Message}）。");
            }
        }
        // ══════════════════ P3：箭头与图钉 ══════════════════

        /// <summary>
        /// 给**某一个阵营**的存活者发一支指向破坏点的断电箭头。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 照抄原版 <c>DeviceManager.BroadcastFuseArrow</c>（0.1.18 :166870）的包形状：
        /// <c>S_NOTIFY_ARROW{FuseboxArrow, Pos}</c>。原版是 <c>BroadcastAlivePlayers</c>（全体），
        /// 这里改成**逐人单发**，按 <paramref name="toDark"/> 只发给对应阵营 ——
        /// 2026-10-20 用户口径：破坏发生后黑方立刻知道，白方延迟一段时间才发现。
        /// </para>
        /// <para>刻意**不发任何文字通知** —— 只给箭头（需求口径）。</para>
        /// </remarks>
        internal static void BroadcastArrow(BreakerDevice device, bool toDark)
        {
            var room = Server.Game.GameRoom.Instance;
            if (device == null || device.DeviceInfo == null || device.DeviceInfo.Pos == null)
            {
                return;
            }

            if (room == null || room.Players == null)
            {
                return;
            }

            try
            {
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p == null || p.PublicInfo == null || p.Session == null)
                    {
                        continue;
                    }

                    if (!p.IsAlive || p.IsSpectator || p.PublicInfo.IsGhost)
                    {
                        continue;
                    }

                    bool dark = p.Color == EPlayerColor.Dark || p.Color == EPlayerColor.Black;
                    if (dark != toDark)
                    {
                        continue;
                    }

                    p.Session.Send(new S_NOTIFY_ARROW
                    {
                        Type = Protocol.EArrowType.FuseboxArrow,
                        Pos = device.DeviceInfo.Pos.Clone(),
                    });
                }
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：发送箭头失败（{ex.Message}）。");
            }
        }

        /// <summary>撤掉指向破坏点的箭头（修复时、离开 Survive 时）。</summary>
        internal static void BroadcastRemoveArrow(BreakerDevice device)
        {
            var room = Server.Game.GameRoom.Instance;
            if (device == null || device.DeviceInfo == null || device.DeviceInfo.Pos == null || room == null)
            {
                return;
            }

            try
            {
                room.BroadcastAlivePlayers(new S_REMOVE_ARROW
                {
                    Type = Protocol.EArrowType.FuseboxArrow,
                    Pos = device.DeviceInfo.Pos.Clone(),
                });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：撤箭头失败（{ex.Message}）。");
            }
        }

        /// <summary>
        /// 给**黑幕**加 / 撤这个破坏点的「电闸」图钉与平板条目。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 走原版 <c>GameRoom.SendSabotageMission</c>（0.1.18 :174389）—— 它**只发给
        /// <c>GameRoom.MasterMind</c>**，正是我们要的口径（其余人在地图上什么都看不到）。
        /// </para>
        /// <para>
        /// 客户端侧（:30968 / :52763）只是把 <c>deviceId</c> 当字典键与条目匹配用，
        /// 不查任何设备表 ⇒ 传我们的自定义设备 ID 完全安全。
        /// 而 <c>MissionType = ScFusebox</c> 会让它按电箱的 pin 类型与词条渲染
        /// —— 这就是「在地图上显示为电箱」。
        /// </para>
        /// </remarks>
        internal static void SendPin(BreakerState state, BreakerDevice device, bool isAdd)
        {
            var room = Server.Game.GameRoom.Instance;
            if (room == null || state == null || device == null || device.DeviceInfo == null)
            {
                return;
            }

            if (device.DeviceInfo.Pos == null)
            {
                return;
            }

            try
            {
                room.SendSabotageMission(
                    Protocol.ESchoolMission.ScFusebox,
                    state.DeviceId,
                    device.DeviceInfo.Pos,
                    isAdd);
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：图钉{(isAdd ? "添加" : "撤除")}失败（{ex.Message}）。");
            }
        }

        // ══════════════════ 时间污染：出站副本改写 ══════════════════

        /// <summary>
        /// 把带「显示时刻」标记的线索换成冻结时间，返回**改写后的副本**；无需改写时返回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 只处理两条**真正喂界面**的包：
        /// <list type="bullet">
        /// <item><c>S_SCAN_DEVICE</c>（<c>Device.Scan</c> :166318，按引用 <c>AddRange(ClueList)</c>）；</item>
        /// <item><c>S_ADD_MY_CLUES</c>（:174520-174540，平板主列表 / 调查开场推送）。</item>
        /// </list>
        /// ⚠ 原版这两条塞的是 <c>ClueList</c> 里的**活对象** ⇒ 只克隆带标记的那几条，
        /// 其余按引用放进新包（序列化只读，不会写回真值）。
        /// </remarks>
        internal static IMessage RewriteOutflow(IMessage packet, int recipient)
        {
            if (packet is S_SCAN_DEVICE scan)
            {
                var copy = new S_SCAN_DEVICE();
                int changed = RewriteClues(scan.InfoList, copy.InfoList, recipient);
                if (changed == 0)
                {
                    return null;
                }

                global::ClueDetail.Core.Log.Info(
                    $"破坏点：出站改写 {changed} 条线索（显示时刻 / 真实当事人，S_SCAN_DEVICE，收件人 {recipient}）。");
                return copy;
            }

            if (packet is S_ADD_MY_CLUES mine)
            {
                var copy = new S_ADD_MY_CLUES();
                int changed = RewriteClues(mine.InfoList, copy.InfoList, recipient);
                if (changed == 0)
                {
                    return null;
                }

                global::ClueDetail.Core.Log.Info(
                    $"破坏点：出站改写 {changed} 条线索（显示时刻 / 真实当事人，S_ADD_MY_CLUES，收件人 {recipient}）。");
                return copy;
            }

            // 发信机（DeviceChat）的**消息发送时间**：S_CHAT_MESSAGE.Time（字段号 5）。
            // 服务端 :177846 写入、客户端 :43918 取出、:86010 用 Util.FormatTimeSentence 渲染。
            // ⚠️ 必须 Clone：:177856 发出的与 :177857 存进 DeviceChatLog 的是**同一实例**。
            // ⚠️ 刻意**不碰 SecretChat**：客户端 UI_SecretChatOverlay 有新鲜度判定（:83047，
            //    Time < SurvivalTime-3 直接丢弃）⇒ 冻结密聊会让气泡永不弹出。
            if (packet is S_CHAT_MESSAGE chat && TimePollutionOn)
            {
                if (chat.Type == EChatType.DeviceChat && chat.Time != 0 && chat.Time != FrozenTime)
                {
                    S_CHAT_MESSAGE chatCopy = chat.Clone();
                    chatCopy.Time = FrozenTime;
                    global::ClueDetail.Core.Log.Info(
                        $"破坏点：出站改写 1 条发信机留言的发送时间（{chat.Time} → 冻结 {FrozenTime} 秒）。");
                    return chatCopy;
                }
            }

            if (packet is S_ADD_CLUE add)
            {
                if (!TryRewriteClue(add.Info, recipient, out PropositionInfo info))
                {
                    return null;
                }

                S_ADD_CLUE copy = add.Clone();
                copy.Info = info;
                global::ClueDetail.Core.Log.Info(
                    $"破坏点：出站改写 1 条线索（显示时刻 / 真实当事人，S_ADD_CLUE，收件人 {recipient}）。");
                return copy;
            }

            if (packet is S_REMOVE_CLUE remove)
            {
                if (!TryRewriteClue(remove.Info, recipient, out PropositionInfo info))
                {
                    return null;
                }

                S_REMOVE_CLUE copy = remove.Clone();
                copy.Info = info;
                global::ClueDetail.Core.Log.Info(
                    $"破坏点：出站改写 1 条线索（显示时刻 / 真实当事人，S_REMOVE_CLUE，收件人 {recipient}）。");
                return copy;
            }

            return null;
        }

        /// <summary>
        /// 单条线索的改写（供 <c>S_ADD_CLUE</c> / <c>S_REMOVE_CLUE</c> 这类"只带一个 Info"的包用）。
        /// </summary>
        private static bool TryRewriteClue(PropositionInfo info, int recipient, out PropositionInfo copy)
        {
            copy = info;
            if (info == null)
            {
                return false;
            }

            int display = ClueDetail.Core.UnknownFields.GetDisplayTime(info);
            int owner = ClueDetail.Core.UnknownFields.GetRealOwner(info);

            // ⚠️ 2026-10-20 回退（用户实测「时钟破坏后所有人看到的记录线索时间都是真实的」）：
            // **不能**在这里判断 TimePollutionOn！它是 `Clock.Broken && State == Survive`，
            // 而线索列表恰恰主要在**调查阶段**（State == Detective）才推送/查看
            // （S_ADD_MY_CLUES :174520、S_SCAN_DEVICE :166318）—— 那时它必为 false，
            // 所有污染线索的时间改写集体失效、显示真值，时间破坏等于没做。
            // 正确判据是「**这条线索是否记录于污染期间**」，而它已经体现为 101 标记是否存在：
            // 101 只在 TimePollutionOn 为真时写入（见 StampPollution），所以有 101 就该改写。
            bool needTime = display != 0;
            bool needOwner = owner != 0;
            if (!needTime && !needOwner)
            {
                return false;
            }

            copy = info.Clone();
            if (needTime)
            {
                copy.Time = display;
            }

            if (needOwner)
            {
                // 当事人本人看实名；其他人看**配置的占位符**（某人 0 / 黑 66613 / 黑幕 11037）。
                // ⚠️ 2026-10-20 起「某人」档的真值不再写成 0（见 IdentityPolluteHook 的注释），
                // 所以必须在这里显式给**非当事人**换成占位符，否则别人会直接读到真实 id。
                // ⚠️ 103 在网线上一律清掉（否则装了本模块的别人能自己读出来是谁）。
                copy.PlayerId = recipient == owner ? owner : PlaceholderPlayerId;
                ClueDetail.Core.UnknownFields.ClearValue(
                    copy, ClueDetail.Core.UnknownFields.RealOwnerFieldNumber);
            }

            return true;
        }

        /// <summary>把 <paramref name="src"/> 逐条搬到 <paramref name="dst"/>，带标记的改时间；返回改写条数。</summary>
        private static int RewriteClues(
            RepeatedField<PropositionInfo> src, RepeatedField<PropositionInfo> dst, int recipient)
        {
            int changed = 0;
            for (int i = 0; i < src.Count; i++)
            {
                var info = src[i];
                if (info == null)
                {
                    continue;
                }

                int display = ClueDetail.Core.UnknownFields.GetDisplayTime(info);
                int owner = ClueDetail.Core.UnknownFields.GetRealOwner(info);

                // 与 TryRewriteClue 同口径（2026-10-20 回退）：
                // 时间改写只看「有没有 101 标记」，**不看当前是否仍在污染** ——
                // 线索列表主要在调查阶段才推送，那时 TimePollutionOn 已经是 false。
                bool needTime = display != 0;
                bool needOwner = owner != 0;
                if (!needTime && !needOwner)
                {
                    dst.Add(info);
                    continue;
                }

                var copy = info.Clone();
                if (needTime)
                {
                    copy.Time = display;
                }

                if (needOwner)
                {
                    // 当事人本人看实名，其他人看配置的占位符；
                    // 网线上一律不带 103（否则装了本模块的别人能自己读出来）。
                    copy.PlayerId = recipient == owner ? owner : PlaceholderPlayerId;
                    ClueDetail.Core.UnknownFields.ClearValue(
                        copy, ClueDetail.Core.UnknownFields.RealOwnerFieldNumber);
                }

                dst.Add(copy);
                changed++;
            }

            return changed;
        }
    }
}