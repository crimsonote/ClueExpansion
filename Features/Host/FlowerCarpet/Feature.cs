using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.FlowerCarpet
{
    /// <summary>
    /// 彩蛋：在尸体周围的地面上撒满花朵（可拾取物品）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个触发点，各自可开关：白方**任务完成度**达到阈值；进入**调查阶段**（Detective）。
    /// 每局只撒一次，换局（回到 Survive）重置。
    /// </para>
    /// <para>
    /// <b>位置合法性（四重，缺一不可）</b>：
    /// </para>
    /// <list type="number">
    /// <item>
    /// <c>AreaManager.ValidPosition</c>（0.1.18 :176524）：读 <c>Managers.Data.MapArray</c>，
    /// 格子边长 **224**，值为 **0 表示不可通行**、非 0 才铺得下东西。这是唯一的"地板"判据。
    /// </item>
    /// <item>
    /// <c>AreaManager.GetArea(pos).Data.DataId == 尸体所在房间</c>：只在尸体所处的**同一个房间**撒，
    /// 不会顺着走廊撒到隔壁去。⚠️ 房间 id 必须由采样中心**自己解析**
    /// （<c>GetArea(center).Data.DataId</c>）—— 服务端 <c>Corpse.RoomID</c> 恒为 0，不能用。
    /// </item>
    /// <item>
    /// 实体碰撞盒：设备（<c>Managers.Device.Devices</c>）与地图本体
    /// （<c>Managers.Map.MapRoot</c> 下的 <c>BoxCollider2D</c>）都不许压。
    /// </item>
    /// <item>
    /// 与**已经撒下的花**保持 <see cref="MinGap"/> 距离（只是装饰密度，不是避障手段）。
    /// </item>
    /// </list>
    /// <para>
    /// <b>为什么花是可拾取物品</b>：`EItemState.Drop` + `EDeviceType.ItemHolder` 是原版"掉在地上的物品"
    /// 的表示方式（`ItemManager.CreateAndDropItem` :175739 就是这么做的）。玩家能捡走 —— 彩蛋的代价，接受。
    /// 本实现**故意绕开** `CreateAndDropItem`：它每次都会 `BroadcastWorldSFX(DropItemSfx)`，
    /// 一具尸体撒几十朵就是几十次音效叠加，所以这里自己构造同样的包、只发一次 `S_SPAWN_DEVICE`。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueFlowerCarpet",
        "彩蛋（纯服务端）：白方任务完成度达到阈值、或进入调查阶段时，在所有尸体周围的地面上随机撒满花朵。" +
        "位置用「地图网格可通行 + 同一房间 + 与既有设备保持最小间距」三重过滤，避开墙壁与家具。" +
        "花是可拾取物品，换局自动失效（服务器不主动清理，由原版 Clear 设备流程带走）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class FlowerCarpetFeature
    {
        // ══════════════════ 配置 ══════════════════

        /// <summary>是否在「白方任务完成度达到阈值」时触发。</summary>
        public static ConfigEntry<bool> TriggerOnMission { get; private set; }

        /// <summary>任务完成度阈值（0..1）。</summary>
        public static ConfigEntry<float> MissionThreshold { get; private set; }

        /// <summary>是否在「进入调查阶段」时触发。</summary>
        public static ConfigEntry<bool> TriggerOnDetective { get; private set; }

        /// <summary>
        /// 撒花密度：每 10000 平方游戏单位的期望朵数（2026-10-21 起取代旧的「固定 40 朵」）。
        /// </summary>
        /// <remarks>
        /// 用户口径：**要固定密度，不是固定总量** —— 半径调大时花数应跟着涨，房间小时不该挤成一团。
        /// 总量由 <c>min(采样圆面积, 尸体所在房间面积) × Density / 10000</c> 算出，再受
        /// <see cref="MaxFlowers"/> 封顶。默认 0.8 ≈ 半径 400 的圆里 40 朵（与旧默认等价）。
        /// </remarks>
        public static ConfigEntry<float> Density { get; private set; }

        /// <summary>每具尸体的朵数上限（防半径调大后爆炸）。</summary>
        public static ConfigEntry<int> MaxFlowers { get; private set; }

        /// <summary>采样半径（游戏单位，以尸体位置为圆心）。</summary>
        public static ConfigEntry<float> Radius { get; private set; }

        /// <summary>与任何既有设备（含其它花）的最小间距（游戏单位）。</summary>
        public static ConfigEntry<float> MinGap { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            TriggerOnMission = config.Bind(
                section,
                "TriggerOnMission",
                true,
                "是否在「白方任务完成度」达到 MissionThreshold 时撒花。\n" +
                "完成度取 MissionManager.CurrentPoint / GoalPoint（原版逃生进度），每局只触发一次。");

            MissionThreshold = config.Bind(
                section,
                "MissionThreshold",
                0.95f,
                new ConfigDescription(
                    "任务完成度阈值（0..1）。默认 0.95 = 95%。\n" +
                    "GoalPoint 为 0（任务尚未初始化）时本项不触发。",
                    new AcceptableValueRange<float>(0.1f, 1f)));

            TriggerOnDetective = config.Bind(
                section,
                "TriggerOnDetective",
                true,
                "是否在「进入调查阶段（Detective）」时撒花。\n" +
                "与 TriggerOnMission 是**或**关系；两者都命中时只撒一次。");

            Density = config.Bind(
                section,
                "Density",
                0.8f,
                new ConfigDescription(
                    "撒花密度：每 10000 平方游戏单位的期望朵数。\n" +
                    "默认 0.8 → 半径 400 的圆里约 40 朵（与旧的「每具 40 朵」等价）。\n" +
                    "调大更密、调小更稀；实际朵数 = min(采样圆面积, 尸体所在房间面积) × 本值 / 10000。\n" +
                    "采样失败（墙里 / 别的房间 / 压家具）会重试，实际可能略少于算出的值。",
                    new AcceptableValueRange<float>(0.05f, 20f)));

            MaxFlowers = config.Bind(
                section,
                "MaxFlowers",
                400,
                new ConfigDescription(
                    "每具尸体的朵数上限（防止半径调大后一次撒出上千朵）。",
                    new AcceptableValueRange<int>(0, 2000)));

            Radius = config.Bind(
                section,
                "Radius",
                400f,
                new ConfigDescription(
                    "采样半径（游戏单位），以尸体位置为圆心。\n" +
                    "400 大约覆盖一间中等房间的核心区域；太大会被房间边界过滤掉大量采样点。",
                    new AcceptableValueRange<float>(50f, 2000f)));

            MinGap = config.Bind(
                section,
                "MinGap",
                80f,
                new ConfigDescription(
                    "与任何既有设备（含其它尸体、已撒下的花）的最小间距（游戏单位）。\n" +
                    "⚠️ 地图网格只描述地板、**不含家具**，所以这一项是避开书架 / 刀架 / 手术台的唯一手段。\n" +
                    "调小会更密，但花会插进家具里；调大会更稀。",
                    new AcceptableValueRange<float>(10f, 300f)));
        }

        // ══════════════════ 取值 ══════════════════

        internal static bool TriggerOnMissionValue => TriggerOnMission == null || TriggerOnMission.Value;

        internal static float ThresholdValue
        {
            get
            {
                float v = MissionThreshold != null ? MissionThreshold.Value : 0.95f;
                return v < 0.1f ? 0.1f : (v > 1f ? 1f : v);
            }
        }

        internal static bool TriggerOnDetectiveValue => TriggerOnDetective == null || TriggerOnDetective.Value;

        /// <summary>每 10000 平方单位的期望朵数（按 0.05..20 夹紧）。</summary>
        internal static float DensityValue
        {
            get
            {
                float v = Density != null ? Density.Value : 0.8f;
                return v < 0.05f ? 0.05f : (v > 20f ? 20f : v);
            }
        }

        /// <summary>单具尸体的朵数上限（0 = 不撒）。</summary>
        internal static int MaxFlowersValue
        {
            get
            {
                int v = MaxFlowers != null ? MaxFlowers.Value : 400;
                return v < 0 ? 0 : v;
            }
        }

        internal static float RadiusValue
        {
            get
            {
                float v = Radius != null ? Radius.Value : 400f;
                return v < 50f ? 50f : v;
            }
        }

        internal static float MinGapValue
        {
            get
            {
                float v = MinGap != null ? MinGap.Value : 80f;
                return v < 10f ? 10f : v;
            }
        }

        // ══════════════════ 常量 ══════════════════

        /// <summary>可用的花：红 / 蓝 / 黄 / 粉（<c>ItemData</c> 1021-1024）。</summary>
        internal static readonly int[] FlowerIds = { 1021, 1022, 1023, 1024 };

        /// <summary>单个采样点的最大重试次数（含被过滤掉的）。</summary>
        internal const int MaxAttemptsPerFlower = 40;

        // ══════════════════ 生命周期 ══════════════════

        /// <summary>插件加载时清空"本局已撒"标记。</summary>
        public static void OnLoaded()
        {
            Reset();
        }

        /// <summary>热关闭时把标记复位，重新打开后能再撒一次。</summary>
        public static void OnDisabled()
        {
            Reset();
        }
    }
}
