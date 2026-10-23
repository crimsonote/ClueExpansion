using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.CorpseMap
{
    /// <summary>
    /// 黑方成功出刀后，把死者的**案发点**直接推给黑方与黑幕。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 需求（2026-10-22）：黑方亲手杀的人，理应立刻知道自己杀了谁、死在哪。
    /// 默认形态是**指向死者的骷髅箭头**（<c>EArrowType.CorpseArrow</c>）—— 纯表现包，
    /// 不动手持物品、不弹界面、不会被施法打断，生存阶段随时可见。
    /// </para>
    /// <para>
    /// 可选的第二种形态：调原版 <c>Corpse.SendScanInfo(player)</c>（0.1.18 :171939）推平板档案，
    /// 让平板地图上直接出现案发点骷髅。但它**有副作用**（默认关闭）：
    /// 客户端 <c>TabletManager.PutCorpseFile</c>（:33490）在活着时会先弹扫描界面、
    /// 再 <c>Inventory.HandSmaho(FileSection)</c> **强制把手里的物品换成平板**；
    /// 如果当时正在施法（例如黑幕拆电箱），扫描界面唤起失败还会让档案根本记不上。
    /// </para>
    /// <para>
    /// <c>SendScanInfo</c> 本身**不碰** <c>_isReport</c> / <c>DiscoverdDone</c> / <c>ClueList</c>，
    /// 所以无论走哪种形态，尸体在服务端都仍是未发现、可扫描状态；之后玩家真去扫，
    /// 客户端 <c>PutCorpseFile</c>（:53232）会按 <c>CorpseId</c> 覆盖同一条档案。
    /// </para>
    /// <para>
    /// 钩子选 <c>DeviceManager.CreateCorpse</c> 而不是 <c>SpawnDevice</c>：
    /// 前者是"黑成功出刀"的唯一收口（原版在那里给凶手发 <c>S_KILL_PLAYER</c>，判定口径
    /// 就是"凶手不是死者本人且还活着"，:167127），后者会被藏尸 / 取出反复触发。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueCorpseMap",
        "纯服务端：黑方成功出刀后，给黑方与黑幕发一个指向死者的骷髅箭头（原版 CorpseArrow），" +
        "生存阶段就知道人死在哪。默认只发箭头 —— 不动手持物品、不弹界面、不怕施法打断；" +
        "平板案发点档案是可选形态，默认关闭（会强制把手里物品换成平板，有副作用）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class CorpseMapFeature
    {
        /// <summary>是否推给黑方（<c>EPlayerColor.Dark</c> / <c>Black</c>）。</summary>
        public static ConfigEntry<bool> NotifyBlack { get; private set; }

        /// <summary>是否推给黑幕（不管他是什么颜色）。</summary>
        public static ConfigEntry<bool> NotifyMastermind { get; private set; }

        /// <summary>
        /// 是否发**指向死者的骷髅箭头**（原版 <c>EArrowType.CorpseArrow</c>）。默认开。
        /// </summary>
        /// <remarks>
        /// 2026-10-22 用户口径：这是默认形态。箭头只是表现，**不动手持物品、不弹 UI、
        /// 不怕施法打断**，生存阶段随时可见。
        /// </remarks>
        public static ConfigEntry<bool> SendArrow { get; private set; }

        /// <summary>
        /// 是否推送**平板尸体档案**（<c>Corpse.SendScanInfo</c>）。默认关。
        /// </summary>
        /// <remarks>
        /// ⚠️ 副作用（用户实测 2026-10-22）：客户端 <c>TabletManager.PutCorpseFile</c>（:33490）
        /// 在**活着**时会先 <c>ScanCorpse()</c> 弹扫描界面、再 <c>Inventory.HandSmaho(FileSection)</c>
        /// **强制把手里物品换成平板** —— 很多时候会打断正常游戏；而且施法 / 拆电箱期间
        /// <c>ScanCorpse()</c> 可能失败，后面的 <c>SetCorpseInfo</c> 就不执行，档案根本记不上。
        /// 所以默认关闭，只保留给"确实想要平板案发点、且能接受这个副作用"的场景。
        /// </remarks>
        public static ConfigEntry<bool> SendFile { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            NotifyBlack = config.Bind(
                section,
                "NotifyBlack",
                true,
                "成功后是否通知黑方（颜色为 Dark / Black 的存活玩家）。");

            NotifyMastermind = config.Bind(
                section,
                "NotifyMastermind",
                true,
                "成功后是否通知黑幕（无论他的颜色）。");

            SendArrow = config.Bind(
                section,
                "SendArrow",
                true,
                "成功后给黑方 / 黑幕发一个**指向死者的骷髅箭头**（原版 EArrowType.CorpseArrow）。\n" +
                "只是屏幕上的指向箭头：不影响手持物品、不弹界面、不会被施法打断。默认开。");

            SendFile = config.Bind(
                section,
                "SendFile",
                false,
                "⚠️ 副作用：开启后会在尸体生成时把**平板尸体档案**（含平板地图上的案发点骷髅）\n" +
                "直接推给黑方 / 黑幕。客户端走原版 PutCorpseFile：\n" +
                "  · 案件发生时**强制把手里的物品换成平板**（Inventory.HandSmaho），很多时候会影响正常游戏；\n" +
                "  · 会额外弹一次扫描界面；\n" +
                "  · 如果当时正在施法（例如黑幕拆电箱），扫描界面唤起失败会让档案**根本记不上**。\n" +
                "因此默认关闭。想要地图案发点又不想被换手，只能等客户端补丁（走 SetCorpseInfo 静默路径）。");
        }

        internal static bool NotifyBlackValue => NotifyBlack == null || NotifyBlack.Value;

        internal static bool NotifyMastermindValue => NotifyMastermind == null || NotifyMastermind.Value;

        internal static bool SendArrowValue => SendArrow == null || SendArrow.Value;

        internal static bool SendFileValue => SendFile != null && SendFile.Value;
    }
}
