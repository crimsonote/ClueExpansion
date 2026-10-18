using ClueDetail.Core;

namespace ClueDetail.Features.Client.FullClue
{
    /// <summary>
    /// 死者 / 旁观者自动获取全部线索。
    /// </summary>
    /// <remarks>
    /// 做法是"补扫"而不是凭空造数据：进入调查阶段后，对本机 <c>Bubble == -1</c>（原版标记为
    /// "有线索可扫描"）的设备逐个补发 <c>C_SCAN_DEVICE</c>。服务端照常回 <c>S_SCAN_DEVICE</c>，
    /// 客户端既拿到了线索本身，也顺手建立了「线索 ↔ 设备」关联 —— 这正是判明详细活动所需的信息，
    /// 而且全程只用原版协议，对未装本模块的房主同样有效（死者本来就能扫）。
    /// <para>
    /// 旁观者原版会被服务端 <c>DeviceManager.Scan</c> 的 <c>IsSpectator</c> 守卫挡掉；
    /// 房主装了本模块时由 <c>Features/Host/SpectatorScan</c> 放行。房主没装时这些请求没有响应，
    /// <see cref="ActivityMap.MatchesPendingRoom"/> 的队首校验会把错位挡在外面。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueGhostFullScan",
        "死者 / 旁观者自动获取全部线索：调查阶段对本机可调查的设备逐个补发扫描请求，既补齐线索，也建立线索与设备的关联。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal partial class FullClueFeature
    {
    }
}
