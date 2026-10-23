using System.Collections.Generic;
using UnityEngine;
using Protocol;
using ServerAreaManager = Server.Game.AreaManager;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGameRoom = Server.Game.GameRoom;
using ServerItemManager = Server.Game.ItemManager;
using ServerObjectUtils = Server.ObjectUtils;

namespace ClueDetail.Features.Host.FlowerCarpet
{
    /// <summary>
    /// 撒花的执行逻辑：找尸体 → 在它所在房间的地板上采样 → 放一朵花。
    /// </summary>
    internal partial class FlowerCarpetFeature
    {
        /// <summary>本局是否已经撒过（每局一次，回到 Survive / 大厅时复位）。</summary>
        private static bool _scattered;

        /// <summary>采样用的随机数（服务端主线程，单实例足够）。</summary>
        private static readonly global::System.Random Rng = new global::System.Random();

        /// <summary>复位"本局已撒"标记。</summary>
        internal static void Reset()
        {
            _scattered = false;
        }

        /// <summary>
        /// 每秒检查一次白方任务完成度（由 <c>GameRoom.SurvivalTick</c> 的 Postfix 驱动）。
        /// </summary>
        /// <remarks>
        /// 完成度取 <c>MissionManager.CurrentPoint / GoalPoint</c>（原版逃生进度）。
        /// <c>GoalPoint</c> 为 0 说明任务还没初始化 ⇒ 不触发，避免除零与"一开局就撒"。
        /// </remarks>
        internal static void CheckMissionProgress()
        {
            if (_scattered || !TriggerOnMissionValue)
            {
                return;
            }

            float current, goal;
            if (!TryReadMissionProgress(out current, out goal))
            {
                return;
            }

            if (goal <= 0f || current <= 0f)
            {
                return;
            }

            if (current / goal >= ThresholdValue)
            {
                global::ClueDetail.Core.Log.Info(
                    $"彩蛋：任务完成度 {current:F0}/{goal:F0} 已达 {ThresholdValue * 100f:F0}%，开始撒花。");
                Scatter();
            }
        }


        // ══════════════════ 任务进度（反射，类的可见性不够） ══════════════════

        private static bool _missionResolved;
        private static object _missionInstance;
        private static global::System.Reflection.PropertyInfo _missionCurrent;
        private static global::System.Reflection.PropertyInfo _missionGoal;

        /// <summary>
        /// 读白方任务进度（<c>MissionManager.CurrentPoint / GoalPoint</c>）。
        /// </summary>
        /// <remarks>
        /// ⚠️ <c>Server.Game.MissionManager</c> 是 **internal** 类（0.1.18 :169148），
        /// 外部程序集直接引用会 CS0122 ⇒ 只能反射。类型与属性缓存一次（它们不会变），
        /// 之后每秒只做两次 <c>GetValue</c>。
        /// </remarks>
        private static bool TryReadMissionProgress(out float current, out float goal)
        {
            current = 0f;
            goal = 0f;

            try
            {
                if (!_missionResolved)
                {
                    _missionResolved = true;
                    global::System.Type t = global::HarmonyLib.AccessTools.TypeByName("Server.Game.MissionManager");
                    if (t != null)
                    {
                        _missionInstance = t.GetProperty(
                            "Instance",
                            global::System.Reflection.BindingFlags.Public
                            | global::System.Reflection.BindingFlags.Static)?.GetValue(null);
                        _missionCurrent = t.GetProperty("CurrentPoint");
                        _missionGoal = t.GetProperty("GoalPoint");
                    }
                }

                if (_missionInstance == null || _missionCurrent == null || _missionGoal == null)
                {
                    return false;
                }

                current = global::System.Convert.ToSingle(_missionCurrent.GetValue(_missionInstance, null));
                goal = global::System.Convert.ToSingle(_missionGoal.GetValue(_missionInstance, null));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>给**所有**尸体周围撒花。每局只执行一次。</summary>
        internal static void Scatter()
        {
            if (_scattered)
            {
                return;
            }

            var dm = ServerDeviceManager.Instance;
            var am = ServerAreaManager.Instance;
            var im = ServerItemManager.Instance;
            var room = ServerGameRoom.Instance;
            if (dm == null || dm.Objects == null || am == null || im == null || room == null)
            {
                return;
            }

            int corpses = 0;
            int placed = 0;

            // 本次新撒下的花不在 dm.Objects 里（它们只是 AreaInfo 上的 DeviceInfo），
            // 所以必须自己维护一份，避免两朵花叠在同一个点上。
            var holders = new List<DeviceInfo>();
            CollectObstacles();      // 房主客户端场景里的碰撞箱（墙 / 家具）

            // ⚠️ 尸体**不在** dm.Objects 里：它们单独存在 DeviceManager._corpses，
            // 通过 `Corpses`（public IReadOnlyList<Corpse>，0.1.18 :166594）读取。
            for (int i = 0; i < dm.Corpses.Count; i++)
            {
                global::Server.Game.Corpse corpse = dm.Corpses[i];
                if (corpse == null)
                {
                    continue;
                }
                PosInfo pos = corpse.DeadPos;
                if (pos == null && corpse.DeviceInfo != null)
                {
                    pos = corpse.DeviceInfo.Pos;
                }

                if (pos == null)
                {
                    continue;
                }

                corpses++;
                placed += ScatterOne(am, im, dm, room, pos, holders);
            }

            if (corpses == 0)
            {
                return;         // 还没有尸体 ⇒ **不置标记**，等有尸体了再撒
            }

            _scattered = true;

            global::ClueDetail.Core.Log.Info(
                $"彩蛋：在 {corpses} 具尸体所在房间的地板上撒了 {placed} 朵花。");
        }

        /// <summary>
        /// 一轮采样的分层计数，用于「一朵都没撒下」时定位卡在哪一层。
        /// </summary>
        private struct PassStats
        {
            /// <summary>尝试过的采样点总数。</summary>
            internal int Samples;

            /// <summary>① 网格判定拒绝（落在地图外 / 值为 0 的墙格）。</summary>
            internal int Grid;

            /// <summary>② 房间判定拒绝（不在尸体所在房间）。</summary>
            internal int Room;

            /// <summary>③ 实体碰撞箱拒绝（压在墙 / 家具上）。</summary>
            internal int Block;

            /// <summary>④ 花间距拒绝（离已撒的花太近）。</summary>
            internal int Gap;

            /// <summary>通过全部判据并成功建出的花。</summary>
            internal int Placed;

            /// <summary>通过全部判据但建物品失败。</summary>
            internal int DropFail;
        }

        /// <summary>围绕一个位置撒花；返回实际放下的数量。朵数按**密度 × 可撒面积**算。</summary>
        private static int ScatterOne(
            ServerAreaManager am,
            ServerItemManager im,
            ServerDeviceManager dm,
            ServerGameRoom room,
            PosInfo center,
            List<DeviceInfo> holders)
        {
            if (MaxFlowersValue <= 0)
            {
                return 0;
            }

            // ⚠️ 房间 id 必须**自己解析**，不能用 `corpse.RoomID`。
            // 0.1.18 :166241 `Device.RoomID` 带 private set，全类里唯一的写入口是
            // `Device.SetInfo(Data.DeviceData)`（:166333 `GetArea(DeviceData.Pos).Data.DataId`）；
            // 而 Corpse 走的是构造函数 + `DeviceManager.SpawnDevice`（:166688），
            // 那条路**从不调用 `SetInfo(DeviceData)`**（Corpse 也没有 DeviceData）⇒
            // `corpse.RoomID` 恒为默认值 **0**，永远不等于 `area.Data.DataId`（1000xx）。
            // 2026-10-20 的「撒了 0 朵花」就是它：两轮采样全部卡在房间判定上，
            // 没有异常、没有 Warn，看起来像"位置判定太严"。
            Server.Game.Area centerArea = am.GetArea(center);
            if (centerArea == null || centerArea.Data == null)
            {
                global::ClueDetail.Core.Log.Warn(
                    $"彩蛋：尸体位置 ({center.X:F0},{center.Y:F0}) 不在任何房间的网格里，跳过。");
                return 0;
            }

            int roomId = centerArea.Data.DataId;

            // 朵数不再固定：按「密度 × 可撒面积」算，半径调大 / 房间变大都跟着涨。
            // 可撒面积取 min(采样圆面积, 房间面积) —— 小房间不会被撒到墙外去，大房间也不会白采。
            int want = FlowersFor(centerArea);

            // 第一轮：严格（网格 + 房间 + 碰撞 + 间距）。
            PassStats strict;
            int placed = ScatterPass(am, im, dm, room, center, roomId, holders, want, true, out strict);

            // 第二轮：降级，**不要求网格**。
            // 网格（MapArray）只覆盖"有瓦片定义"的地板，个别房间边缘会整片判 0；
            // 降级后仍要求"同一房间 + 不压家具"，只是不再相信网格。
            PassStats lenient = default(PassStats);
            if (placed == 0)
            {
                placed = ScatterPass(am, im, dm, room, center, roomId, holders, want, false, out lenient);
            }

            // 只在彻底失败时打一条分层明细：正常局不会刷屏，出问题时不用再猜。
            if (placed == 0)
            {
                global::ClueDetail.Core.Log.Warn(
                    $"彩蛋：房间 {roomId} 的尸体 ({center.X:F0},{center.Y:F0}) 一朵都没撒下 —— " +
                    $"严格轮 {strict.Samples} 次采样（网格拒 {strict.Grid} / 房间拒 {strict.Room} / " +
                    $"碰撞拒 {strict.Block} / 间距拒 {strict.Gap} / 建物失败 {strict.DropFail}）；" +
                    $"降级轮 {lenient.Samples} 次采样（房间拒 {lenient.Room} / 碰撞拒 {lenient.Block} / " +
                    $"间距拒 {lenient.Gap} / 建物失败 {lenient.DropFail}）。");
            }

            return placed;
        }

        /// <summary>
        /// 这一具尸体该撒多少朵：<c>min(采样圆面积, 房间面积) × Density / 10000</c>，再受上限封顶。
        /// </summary>
        /// <remarks>
        /// 用户口径 2026-10-21：**要固定密度，不是固定总量**。
        /// 旧实现写死 40 朵 ⇒ 半径调大就变稀、小房间又显得挤。
        /// 现在朵数跟着面积走，`MinGap` 仍负责"花与花之间别叠"。
        /// </remarks>
        private static int FlowersFor(Server.Game.Area area)
        {
            float radius = RadiusValue;
            float circle = (float)(global::System.Math.PI * radius * radius);
            float room = RoomArea(area);
            float usable = room > 0f && room < circle ? room : circle;

            int want = (int)global::System.Math.Round(usable * DensityValue / 10000f);
            if (want < 1)
            {
                want = 1;
            }

            int max = MaxFlowersValue;
            return want < max ? want : max;
        }

        /// <summary>房间面积（游戏单位²）：数 <c>MapArray</c> 里属于该房间类型的格数 × 224²。</summary>
        /// <remarks>
        /// 服务端 <c>Area</c> 只带 <c>RoomData</c>，没有格数；而 <c>MapArray</c> 是现成的
        /// （<c>AreaManager.ValidPosition</c> 读的就是它，0.1.18 :176524）。
        /// 每局只调一次，1927 格的遍历可以忽略。
        /// </remarks>
        private static float RoomArea(Server.Game.Area area)
        {
            try
            {
                var data = Managers.Data;
                if (data == null || data.MapArray == null || data.MapData == null
                    || area == null || area.Data == null)
                {
                    return 0f;
                }

                int w = (int)data.MapData.MapSize.X;
                int h = (int)data.MapData.MapSize.Y;
                int type = (int)area.Data.Type;
                var map = data.MapArray;

                int cells = 0;
                for (int x = 0; x < w && x < map.Count; x++)
                {
                    var column = map[x];
                    if (column == null)
                    {
                        continue;
                    }

                    for (int y = 0; y < h && y < column.Count; y++)
                    {
                        if (column[y] == type)
                        {
                            cells++;
                        }
                    }
                }

                return cells * 224f * 224f;
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "彩蛋：估算房间面积失败，回退到采样圆面积。");
                return 0f;
            }
        }

        /// <summary>一轮采样；<paramref name="requireGrid"/> 为 false 时跳过网格判据。</summary>
        private static int ScatterPass(
            ServerAreaManager am,
            ServerItemManager im,
            ServerDeviceManager dm,
            ServerGameRoom room,
            PosInfo center,
            int roomId,
            List<DeviceInfo> holders,
            int want,
            bool requireGrid,
            out PassStats stats)
        {
            float radius = RadiusValue;
            float minGapSq = MinGapValue * MinGapValue;
            int placed = 0;
            stats = default(PassStats);

            for (int n = 0; n < want; n++)
            {
                for (int attempt = 0; attempt < MaxAttemptsPerFlower; attempt++)
                {
                    float x = center.X + (float)((Rng.NextDouble() * 2.0 - 1.0) * radius);
                    float y = center.Y + (float)((Rng.NextDouble() * 2.0 - 1.0) * radius);
                    var p = new PosInfo { X = x, Y = y };
                    stats.Samples++;

                    // ① 地图网格：0 = 不可通行（墙 / 地图外）。降级轮跳过。
                    if (requireGrid && !am.ValidPosition(p))
                    {
                        stats.Grid++;
                        continue;
                    }

                    // ② 必须在尸体所在的那个房间
                    var area = am.GetArea(p);
                    if (area == null || area.Data == null || area.Data.DataId != roomId)
                    {
                        stats.Room++;
                        continue;
                    }

                    // ③ 真实碰撞箱：房主进程里客户端场景是活的，直接读 Collider2D.bounds
                    if (Blocked(x, y))
                    {
                        stats.Block++;
                        continue;
                    }

                    // ④ 花与花之间别叠在一起（装饰密度，不是障碍）
                    if (TooClose(holders, p, minGapSq))
                    {
                        stats.Gap++;
                        continue;
                    }

                    DeviceInfo holder = DropFlower(im, am, room, p);
                    if (holder != null)
                    {
                        holders.Add(holder);
                        placed++;
                        stats.Placed++;
                        break;      // 这朵已经放下，换下一朵
                    }

                    // 建物品失败就继续重试：可能只是这一帧的状态问题。
                    stats.DropFail++;
                }
            }

            return placed;
        }

        /// <summary>采样点是否离某台设备（或已撒的花）太近。</summary>
        /// <summary>只负责「花与花之间别叠在一起」；障碍判定交给 <see cref="Blocked"/>。</summary>
        private static bool TooClose(List<DeviceInfo> holders, PosInfo p, float minGapSq)
        {
            for (int i = 0; i < holders.Count; i++)
            {
                PosInfo hp = holders[i].Pos;
                if (hp != null && DistSq(hp, p) < minGapSq)
                {
                    return true;
                }
            }

            return false;
        }

        // ══════════════════ 碰撞箱（房主专属：服务端与客户端同进程） ══════════════════

        /// <summary>本局收集到的实体碰撞体（只用在这些花上，别拿去干别的）。</summary>
        private static readonly List<Collider2D> Obstacles = new List<Collider2D>();

        /// <summary>
        /// 收集客户端场景里所有**非触发器**碰撞体（墙、家具）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么能这么做：房主既是服务端也是客户端，同一个进程、同一个场景 ⇒
        /// 这些 <c>Collider2D</c> 是活的，几何可以直接读。
        /// </para>
        /// <para>
        /// ⚠️ **不能**用 <c>Physics2D.OverlapPoint(..., LayerMask.GetMask("Block"))</c>：
        /// 游戏的 AOI 剔除是**改 <c>gameObject.layer</c>** 实现的（0.1.18 :31466 把物体挪到 19 层），
        /// 远处房间的家具会被挪出 Block 层 ⇒ 查不到 ⇒ 花直接穿模。
        /// 直接读 <c>Collider2D.bounds</c>（世界坐标 AABB）不受层和剔除影响。
        /// </para>
        /// <para>
        /// `includeInactive: true` 是必需的：被剔除的物体可能整块 SetActive(false)。
        /// </para>
        /// <para>
        /// 两个来源：① <c>Managers.Device.Devices</c>（设备 / 掉落物 / 尸体）；
        /// ② <c>Managers.Map.MapRoot</c>（墙 / 隔断 / 家具）。后者**不在**设备表里 ——
        /// 只靠 ① 的话花会插进书架、手术台。地图本体由
        /// <c>MapManager.GetParent</c>（0.1.18 :30549）为每间房建一个 <c>@&lt;roomId&gt;</c> 根节点，
        /// 上面挂 <c>CompositeCollider2D</c> 把子碰撞盒合批（:30554）。
        /// </para>
        /// <para>
        /// ⚠️ 第二个来源**只收 <c>BoxCollider2D</c>**：<c>CompositeCollider2D.bounds</c> 是
        /// **整间房的 AABB**，把它当障碍会把整间房判死（一朵都撒不出）。
        /// 子 <c>BoxCollider2D</c> 的几何独立于合批，仍在原来的世界位置。
        /// </para>
        /// </remarks>
        private static void CollectObstacles()
        {
            Obstacles.Clear();

            // ① 设备 / 掉落物 / 尸体自身携带的碰撞盒。
            var dm = Managers.Device;
            if (dm != null && dm.Devices != null)
            {
                foreach (var pair in dm.Devices)
                {
                    var dev = pair.Value;
                    if (dev == null || dev.gameObject == null)
                    {
                        continue;
                    }

                    // 尸体本身不算障碍（我们要在它周围撒花）。
                    if (dev is global::Corpse)
                    {
                        continue;
                    }

                    var cols = dev.gameObject.GetComponentsInChildren<Collider2D>(true);
                    for (int i = 0; i < cols.Length; i++)
                    {
                        var c = cols[i];
                        if (c != null && !c.isTrigger)
                        {
                            Obstacles.Add(c);
                        }
                    }
                }
            }

            // ② 地图本体（墙 / 隔断 / 家具）。
            var map = Managers.Map;
            Transform root = map != null ? map.MapRoot : null;
            if (root == null)
            {
                return;
            }

            var boxes = root.GetComponentsInChildren<BoxCollider2D>(true);
            for (int i = 0; i < boxes.Length; i++)
            {
                var c = boxes[i];
                if (c == null || c.isTrigger)
                {
                    continue;
                }

                // 设备 / 掉落物 / 尸体已在 ① 收过，这里跳过，避免重复。
                if (c.GetComponentInParent<DeviceBase>() != null)
                {
                    continue;
                }

                Obstacles.Add(c);
            }
        }

        /// <summary>该点是否被任何实体碰撞体覆盖（墙 / 家具）。</summary>
        private static bool Blocked(float x, float y)
        {
            var p = new Vector3(x, y, 0f);
            for (int i = 0; i < Obstacles.Count; i++)
            {
                var c = Obstacles[i];
                if (c != null && c.bounds.Contains(p))
                {
                    return true;
                }
            }

            return false;
        }

        private static float DistSq(PosInfo a, PosInfo b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// 在指定位置放一朵花，返回它的占位（<c>DeviceInfo</c>）；失败返回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 刻意**不用** <c>ItemManager.CreateAndDropItem</c>：它每调用一次就
        /// <c>BroadcastWorldSFX(DropItemSfx)</c>，一具尸体几十朵就是几十次音效叠加。
        /// 这里只复刻它的数据动作（建 Item → 进列表 → SyncItem → 建 ItemHolder → 广播生成），
        /// 跳过音效。
        /// </remarks>
        private static DeviceInfo DropFlower(
            ServerItemManager im, ServerAreaManager am, ServerGameRoom room, PosInfo pos)
        {
            try
            {
                int dataId = FlowerIds[Rng.Next(FlowerIds.Length)];
                var item = ServerObjectUtils.CreateItem(dataId, Define.EItemState.Drop);
                if (item == null || item.Info == null)
                {
                    return null;
                }

                im.Items.Add(item);
                im.SyncItem(item);

                var holder = new DeviceInfo
                {
                    Type = EDeviceType.ItemHolder,
                    DeviceId = item.Info.ObjectId,
                    Pos = pos,
                };
                holder.StateList.Add(item.Info.DataId);

                var area = am.GetArea(pos);
                if (area != null && area.Info != null)
                {
                    area.Info.DeviceInfos.Add(holder);
                }

                room.Broadcast(new S_SPAWN_DEVICE { Device = holder });
                return holder;
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"彩蛋：撒花失败（{ex.Message}）。");
                return null;
            }
        }
    }
}