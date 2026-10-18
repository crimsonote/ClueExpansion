using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.UI.LobbyToggle
{
    /// <summary>
    /// 在大厅「详细设置」的「其它」页里加一行开关：线索详细活动。
    /// </summary>
    /// <remarks>
    /// 做法与 HideAndSeek 的设置页一致：**克隆原版的开关行**，换标题、重绑控件，
    /// 而不是自己拼 prefab。模板行取原版 ETC 页里已有的一个开关行（见 <see cref="LobbyToggle"/>）。
    /// <para>
    /// 只有房主能改（原版房间设置的口径）；非房主看到的是半透明、点不动的同一行。
    /// 这一行本身就是本模块注入的，所以**只有装了本模块的玩家才看得见**。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueLobbyToggle",
        "在大厅「详细设置」的「其它」页加入「线索详细活动」开关（仅房主可改，随房间同步）。只有装了本模块的玩家能看到这一行。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal partial class LobbyToggleFeature
    {
        /// <summary>设置页那一行的标题文案（换语言时只改这里）。</summary>
        public static ConfigEntry<string> RowTitle { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            RowTitle = config.Bind(
                section,
                "RowTitle",
                "线索详细活动",
                "设置页里那一行的标题文案。语言不同想改显示名时改这里即可。");
        }

        /// <summary>
        /// 热关闭：把已经克隆进设置页的那一行撤掉。
        /// </summary>
        /// <remarks>
        /// 热开启时不需要额外动作 —— <c>LobbyToggle.Ensure</c> 下次被调（打开设置页 / 原版刷新）时
        /// 会重新建出来。
        /// </remarks>
        public static void OnDisabled()
        {
            LobbyToggle.Destroy();
        }
    }
}
