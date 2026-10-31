using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.FlowerScene
{
    /// <summary>
    /// 彩蛋：花瓣场景 —— 整张地图按密度铺花瓣与杂物，另按全图配额撒一批特殊物品。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 口径（2026-10-31 用户定义）：
    /// <list type="number">
    /// <item>整图铺**花瓣**（花物品 1021–1024），密度按面积算，可调；</item>
    /// <item>全图**特殊物品总数**固定（默认 24），按房间面积分配，花园旁多摆鱼，
    /// 其中至少一条**金鱼**（1061）；其他房间放铃铛/气喇叭/玩具锤/罐头/奖杯；</item>
    /// <item>**矿山区域**（<c>Mine</c>/<c>MineEdge</c>）放随机矿石（1032–1034），
    /// **不占用**特殊物品名额；</item>
    /// <item>全部放在可通行地面。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 生成方式与 <c>FlowerCarpet</c> 一致：自己构造 <c>S_SPAWN_DEVICE</c>，
    /// 不走 <c>ItemManager.CreateAndDropItem</c>（它每次都播 <c>DropItemSfx</c>，几十个就是音效爆炸）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueFlowerScene",
        "彩蛋（纯服务端）：整张地图按密度铺花瓣与杂物，并撒一批特殊物品（全图总数固定，花园旁多鱼、矿山放矿石）。" +
        "位置用「地图网格可通行 + 同房间 + 实体碰撞避障 + 最小间距」过滤，避开墙壁与家具。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class FlowerSceneFeature
    {
        /// <summary>全图特殊物品总数（金鱼、铃铛、奖杯等；矿石不计入）。</summary>
        public static ConfigEntry<int> TotalSpecialItems { get; private set; }

        /// <summary>花瓣密度：每 10000 平方游戏单位的朵数。</summary>
        public static ConfigEntry<float> PetalDensity { get; private set; }

        /// <summary>矿山矿石密度：每 10000 平方游戏单位的块数（不占特殊物品名额）。</summary>
        public static ConfigEntry<float> OreDensity { get; private set; }

        /// <summary>任意两个铺设物之间的最小间距（游戏单位）。</summary>
        public static ConfigEntry<float> MinGap { get; private set; }

        /// <summary>单个房间的特殊物品上限（防止名额全挤进一间）。</summary>
        public static ConfigEntry<int> MaxPerRoom { get; private set; }

        /// <summary>花瓣是否可以被捡起（false = 纯装饰，服务端拒绝拾取）。</summary>
        public static ConfigEntry<bool> PetalPickable { get; private set; }

        /// <summary>花瓣与物品之间的最小间距（比物品间小，避免花瓣铺满后把物品挤掉）。</summary>
        public static ConfigEntry<float> PetalGap { get; private set; }

        /// <summary>花瓣与花瓣之间的最小间距（独立配置，默认比 PetalGap 大一点）。</summary>
        public static ConfigEntry<float> PetalSpacing { get; private set; }

        /// <summary>花瓣数量硬上限（防止配置写大 + 小间距把全图铺满）。</summary>
        public static ConfigEntry<int> MaxPetals { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            TotalSpecialItems = config.Bind(
                section,
                "TotalSpecialItems",
                24,
                new ConfigDescription(
                    "全图特殊物品总数（鱼 / 铃铛 / 气喇叭 / 玩具锤 / 罐头 / 奖杯）。\n" +
                    "按房间面积分配；矿山矿石不占用这个名额。",
                    new AcceptableValueRange<int>(1, 200)));

            PetalDensity = config.Bind(
                section,
                "PetalDensity",
                0.15f,
                new ConfigDescription(
                    "花瓣密度：每 10000 平方游戏单位的期望朵数。\n" +
                    "0 = 不铺花瓣。默认 0.15（全图约 800 朵）；最终还会被 MaxPetals 封顶。",
                    new AcceptableValueRange<float>(0f, 20f)));

            OreDensity = config.Bind(
                section,
                "OreDensity",
                1.5f,
                new ConfigDescription(
                    "矿山矿石密度：每 10000 平方游戏单位的期望块数。\n" +
                    "只作用于矿山区域，不占特殊物品名额。默认 1.5。",
                    new AcceptableValueRange<float>(0f, 20f)));

            MinGap = config.Bind(
                section,
                "MinGap",
                200f,
                new ConfigDescription(
                    "任意两个铺设物之间的最小距离（游戏单位）。\n" +
                    "防止物品挤在一起。默认 200。",
                    new AcceptableValueRange<float>(20f, 600f)));

            MaxPerRoom = config.Bind(
                section,
                "MaxPerRoom",
                8,
                new ConfigDescription(
                    "单个房间的特殊物品上限。默认 8。",
                    new AcceptableValueRange<int>(1, 50)));

            PetalPickable = config.Bind(
                section,
                "PetalPickable",
                true,
                "花瓣是否可以被捡起。\n" +
                "true = 和普通掉落物一样能捡；false = 纯装饰（服务端拒绝拾取，客户端仍可能显示拾取提示）。");

            PetalGap = config.Bind(
                section,
                "PetalGap",
                100f,
                new ConfigDescription(
                    "花瓣与**物品**之间的最小间距（游戏单位）。\n" +
                    "比 MinGap 小：花瓣是装饰，允许靠近物品。默认 100。",
                    new AcceptableValueRange<float>(0f, 300f)));

            PetalSpacing = config.Bind(
                section,
                "PetalSpacing",
                140f,
                new ConfigDescription(
                    "花瓣与**花瓣**之间的最小间距（游戏单位）。\n" +
                    "独立于 PetalGap：调小更密，调大更稀。默认 140。",
                    new AcceptableValueRange<float>(0f, 400f)));

            MaxPetals = config.Bind(
                section,
                "MaxPetals",
                900,
                new ConfigDescription(
                    "花瓣数量硬上限。\n" +
                    "无论 PetalDensity 算出多少，实际最多铺这么多朵。默认 900（约 800-900 朵，性能安全）。",
                    new AcceptableValueRange<int>(0, 3000)));
        }

        public static void OnLoaded()
        {
            Reset();
        }

        /// <summary>热关闭：把本功能生成的东西全部撤掉。</summary>
        public static void OnDisabled()
        {
            RemoveSpawned();
            Reset();
        }

        internal static int TotalValue => TotalSpecialItems != null
            ? (TotalSpecialItems.Value < 0 ? 0 : TotalSpecialItems.Value)
            : 24;

        internal static float PetalDensityValue => PetalDensity != null
            ? Clamp(PetalDensity.Value, 0f, 20f)
            : 0.15f;

        internal static int MaxPetalsValue => MaxPetals != null
            ? ClampInt(MaxPetals.Value, 0, 3000)
            : 900;

        internal static float OreDensityValue => OreDensity != null
            ? Clamp(OreDensity.Value, 0f, 20f)
            : 1.5f;

        internal static float MinGapValue => MinGap != null
            ? Clamp(MinGap.Value, 20f, 600f)
            : 200f;

        internal static int MaxPerRoomValue => MaxPerRoom != null
            ? ClampInt(MaxPerRoom.Value, 1, 50)
            : 8;

        internal static bool PetalPickableValue => PetalPickable == null || PetalPickable.Value;

        internal static float PetalGapValue => PetalGap != null
            ? Clamp(PetalGap.Value, 0f, 300f)
            : 100f;

        internal static float PetalSpacingValue => PetalSpacing != null
            ? Clamp(PetalSpacing.Value, 0f, 400f)
            : 140f;

        private static float Clamp(float v, float min, float max)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                return min;
            }

            return v < min ? min : (v > max ? max : v);
        }

        private static int ClampInt(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
