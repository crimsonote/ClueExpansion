using System.Collections.Generic;
using UnityEngine;
using Protocol;
using ServerArea = Server.Game.Area;
using ServerAreaManager = Server.Game.AreaManager;
using ServerGameRoom = Server.Game.GameRoom;
using ServerItem = Server.Game.Item;
using ServerItemManager = Server.Game.ItemManager;
using ServerObjectUtils = Server.ObjectUtils;

namespace ClueDetail.Features.Host.FlowerScene
{
    /// <summary>
    /// 花瓣场景的执行逻辑：全图采样 → 按房间类型选物品 → 生成地面物品。
    /// </summary>
    internal partial class FlowerSceneFeature
    {
        private static bool _scattered;

        private static readonly global::System.Random Rng = new global::System.Random();

        /// <summary>本功能生成的地面物品（用于热关闭时撤回）。</summary>
        private static readonly List<DeviceInfo> Spawned = new List<DeviceInfo>();

        private static readonly List<ServerItem> SpawnedItems = new List<ServerItem>();

        // ══════════════════ 物品池 ══════════════════

        /// <summary>花瓣（1021–1024）。</summary>
        private static readonly int[] PetalIds = { 1021, 1022, 1023, 1024 };

        /// <summary>普通杂物：罐头（汽水）、玩具锤、气喇叭、铃铛、奖杯。</summary>
        private static readonly int[] MiscPool = { 3001, 3002, 3003, 3004, 3005, 3007, 3008, 3009, 1062, 1063, 1064 };

        /// <summary>花园的鱼：普通多、稀有少（金鱼单独放一条）。</summary>
        private static readonly int[] FishPool = { 1059, 1059, 1059, 1060 };

        /// <summary>矿山矿石（蓝 / 绿 / 红）。</summary>
        private static readonly int[] OrePool = { 1032, 1033, 1034 };

        private const int FishGoldId = 1061;

        private const int MineType = (int)ERoomType.Mine;
        private const int MineEdgeType = (int)ERoomType.MineEdge;
        private const int ParkType = (int)ERoomType.Park;

        private const float Cell = 224f;

        // ══════════════════ 生命周期 ══════════════════

        internal static void Reset()
        {
            Spawned.Clear();
            SpawnedItems.Clear();
            _scattered = false;
        }

        /// <summary>热关闭：把生成的东西全部撤掉。</summary>
        internal static void RemoveSpawned()
        {
            if (Spawned.Count == 0)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            ServerAreaManager am = ServerAreaManager.Instance;
            ServerItemManager im = ServerItemManager.Instance;

            for (int i = 0; i < Spawned.Count; i++)
            {
                DeviceInfo holder = Spawned[i];
                if (holder == null)
                {
                    continue;
                }

                try
                {
                    room?.Broadcast(new S_REMOVE_DEVICE { DeviceId = holder.DeviceId });
                }
                catch
                {
                    // 撤回失败不阻断其它清理。
                }

                if (am != null && am.Areas != null)
                {
                    for (int a = 0; a < am.Areas.Count; a++)
                    {
                        ServerArea area = am.Areas[a];
                        if (area?.Info?.DeviceInfos != null)
                        {
                            area.Info.DeviceInfos.Remove(holder);
                        }
                    }
                }

                if (im?.ItemHolders != null)
                {
                    im.ItemHolders.Remove(holder);
                }
            }

            for (int i = 0; i < SpawnedItems.Count; i++)
            {
                ServerItem item = SpawnedItems[i];
                if (item != null)
                {
                    im?.Items.Remove(item);
                }
            }

            Spawned.Clear();
            SpawnedItems.Clear();
        }

        // ══════════════════ 铺设 ══════════════════

        /// <summary>进入生存阶段后铺一次；每局只铺一次。</summary>
        internal static void TryScatter()
        {
            if (_scattered)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                return;
            }

            _scattered = true;
            Scatter(room);
        }

        private static void Scatter(ServerGameRoom room)
        {
            try
            {
                var am = ServerAreaManager.Instance;
                var im = ServerItemManager.Instance;
                if (am == null || im == null || Managers.Data == null || Managers.Data.MapArray == null)
                {
                    return;
                }

                CollectObstacles();

                MapStats stats = CountCells();
                if (stats.Walkable <= 0)
                {
                    return;
                }

                // ⚠️ 顺序很重要：先把「特殊物品 / 矿石」放好，再铺花瓣。
                // 反过来的话花瓣（密度高、数量多）会把全图占满，物品的 MinGap 采样必然全部失败 ——
                // 2026-10-31 实测就是「花瓣 868、物品 0、矿石 0」。
                int total = TotalValue;
                int goldPlaced = 0;
                if (total > 0 && PlaceAny(room, im, am, FishGoldId, IsParkCell, MinGapValue, true) != null)
                {
                    goldPlaced = 1;
                }

                int placedSpecial = goldPlaced;
                for (int i = goldPlaced; i < total; i++)
                {
                    if (PlaceAny(room, im, am, -1, null, MinGapValue, true) != null)
                    {
                        placedSpecial++;
                    }
                }

                int ore = (int)(stats.MineCells * Cell * Cell * OreDensityValue / 10000.0);
                ore = clampInt(ore, 0, 200);
                if (stats.MineCells > 0 && ore == 0)
                {
                    ore = 1;    // 矿山再小也至少放一块
                }

                int placedOre = 0;
                for (int i = 0; i < ore; i++)
                {
                    if (PlaceAny(room, im, am, Pick(OrePool), IsMineCell, MinGapValue, true) != null)
                    {
                        placedOre++;
                    }
                }

                int petals = (int)(stats.Walkable * Cell * Cell * PetalDensityValue / 10000.0);
                petals = clampInt(petals, 0, 3000);

                int placedPetal = 0;
                for (int i = 0; i < petals; i++)
                {
                    if (PlaceAny(room, im, am, Pick(PetalIds), null, PetalGapValue, PetalPickableValue) != null)
                    {
                        placedPetal++;
                    }
                }

                global::ClueDetail.Core.Log.Info(
                    $"花瓣场景：花瓣 {placedPetal}、矿石 {placedOre}、特殊物品 {placedSpecial}（金鱼 {(goldPlaced > 0 ? "已放" : "未放")}）。");
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "花瓣场景：铺设失败。");
            }
        }

        /// <summary>
        /// 全图随机采样一次并放置。<paramref name="dataId"/> 为 -1 时按房间类型自动选物品；
        /// <paramref name="accept"/> 非空时只接受满足该判据的格子（矿石/花园）。
        /// </summary>
        private static DeviceInfo PlaceAny(
            ServerGameRoom room,
            ServerItemManager im,
            ServerAreaManager am,
            int dataId,
            global::System.Func<int, bool> accept,
            float gap,
            bool pickable)
        {
            var data = Managers.Data;
            if (data == null || data.MapData == null || data.MapArray == null)
            {
                return null;
            }

            var md = data.MapData;
            int w = (int)md.MapSize.X;
            int h = (int)md.MapSize.Y;
            float minX = md.MapOffset.X * Cell;
            float minY = md.MapOffset.Y * Cell;
            float maxX = (md.MapOffset.X + w) * Cell;
            float maxY = (md.MapOffset.Y + h) * Cell;

            for (int attempt = 0; attempt < 60; attempt++)
            {
                float x = minX + (float)(Rng.NextDouble() * (maxX - minX));
                float y = minY + (float)(Rng.NextDouble() * (maxY - minY));
                var p = new PosInfo { X = x, Y = y };

                if (!am.ValidPosition(p))
                {
                    continue;
                }

                int cellType = CellTypeAt(p);
                if (accept != null && !accept(cellType))
                {
                    continue;
                }

                if (Blocked(x, y) || TooClose(p, gap))
                {
                    continue;
                }

                int id = dataId;
                if (id < 0)
                {
                    id = PickByCell(cellType);
                }

                DeviceInfo holder = DropItem(room, im, am, id, p, pickable);
                if (holder != null)
                {
                    return holder;
                }
            }

            return null;
        }

        private static bool IsMineCell(int cellType)
        {
            return cellType == MineType || cellType == MineEdgeType;
        }

        private static bool IsParkCell(int cellType)
        {
            return cellType == ParkType;
        }

        private static int PickByCell(int cellType)
        {
            if (cellType == ParkType)
            {
                return Pick(FishPool);
            }

            return Pick(MiscPool);
        }

        private static int Pick(int[] pool)
        {
            return pool[Rng.Next(pool.Length)];
        }

        // ══════════════════ 生成物品 ══════════════════

        /// <summary>
        /// 在指定位置放一个地面物品，返回它的占位；失败返回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 与 <c>CreateAndDropItem</c>（0.1.18 :175737）逐字等价，但不播音效。
        /// ⚠️ **必须加入 <c>ItemManager.ItemHolders</c>**：服务端 <c>AcquireItem</c>（:175862）
        /// 只认这个列表里的 id，否则物品显示正常但永远捡不起来。
        /// <paramref name="pickable"/> 为 false 时故意不登记 —— 就成了"看得见但捡不到"的装饰。
        /// </remarks>
        private static DeviceInfo DropItem(
            ServerGameRoom room,
            ServerItemManager im,
            ServerAreaManager am,
            int dataId,
            PosInfo pos,
            bool pickable)
        {
            try
            {
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

                if (pickable && !ContainsHolder(im, holder.DeviceId))
                {
                    im.ItemHolders.Add(holder);
                }

                room.Broadcast(new S_SPAWN_DEVICE { Device = holder });
                Spawned.Add(holder);
                SpawnedItems.Add(item);
                return holder;
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"花瓣场景：生成物品 {dataId} 失败（{ex.Message}）。");
                return null;
            }
        }

        private static bool ContainsHolder(ServerItemManager im, int deviceId)
        {
            var list = im.ItemHolders;
            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].DeviceId == deviceId)
                {
                    return true;
                }
            }

            return false;
        }

        // ══════════════════ 地图统计 ══════════════════

        private struct MapStats
        {
            internal int Walkable;
            internal int MineCells;
        }

        private static MapStats CountCells()
        {
            var stats = new MapStats();
            var data = Managers.Data;
            if (data == null || data.MapData == null || data.MapArray == null)
            {
                return stats;
            }

            int w = (int)data.MapData.MapSize.X;
            int h = (int)data.MapData.MapSize.Y;
            var map = data.MapArray;

            for (int x = 0; x < w && x < map.Count; x++)
            {
                var column = map[x];
                if (column == null)
                {
                    continue;
                }

                for (int y = 0; y < h && y < column.Count; y++)
                {
                    int cell = column[y];
                    if (cell == 0)
                    {
                        continue;
                    }

                    stats.Walkable++;
                    if (cell == MineType || cell == MineEdgeType)
                    {
                        stats.MineCells++;
                    }
                }
            }

            return stats;
        }

        private static int CellTypeAt(PosInfo pos)
        {
            var data = Managers.Data;
            if (data == null || data.MapData == null || data.MapArray == null)
            {
                return 0;
            }

            int x = (int)(pos.X / Cell) - (int)data.MapData.MapOffset.X;
            int y = (int)(pos.Y / Cell) - (int)data.MapData.MapOffset.Y;
            if (x < 0 || y < 0 || x >= data.MapArray.Count || y >= data.MapArray[x].Count)
            {
                return 0;
            }

            return data.MapArray[x][y];
        }

        private static bool TooClose(PosInfo p, float gap)
        {
            float gapSq = gap * gap;
            for (int i = 0; i < Spawned.Count; i++)
            {
                PosInfo hp = Spawned[i]?.Pos;
                if (hp == null)
                {
                    continue;
                }

                float dx = hp.X - p.X;
                float dy = hp.Y - p.Y;
                if (dx * dx + dy * dy < gapSq)
                {
                    return true;
                }
            }

            return false;
        }

        private static int clampInt(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        // ══════════════════ 碰撞箱（房主专属：服务端与客户端同进程） ══════════════════

        private static readonly List<Collider2D> Obstacles = new List<Collider2D>();

        private static void CollectObstacles()
        {
            Obstacles.Clear();

            var dm = Managers.Device;
            if (dm != null && dm.Devices != null)
            {
                foreach (var pair in dm.Devices)
                {
                    var dev = pair.Value;
                    if (dev == null || dev.gameObject == null || dev is global::Corpse)
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

                if (c.GetComponentInParent<DeviceBase>() != null)
                {
                    continue;
                }

                Obstacles.Add(c);
            }
        }

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
    }
}
