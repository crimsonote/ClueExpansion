using ClueDetail.Core;

namespace ClueDetail.Features.Both.SelfCheck
{
    /// <summary>
    /// 启动自检：数据表就绪后输出一次诊断日志。
    /// </summary>
    /// <remarks>
    /// 排查"功能没生效"时，先看这三行就够了：
    /// <list type="number">
    /// <item><b>未知字段通道</b> —— 失败则说明跨端传递断了（服务端下发的活动收不到）；</item>
    /// <item><b>房主开关</b> —— "未收到标记"说明房主没装本模块，客户端会走自采集；</item>
    /// <item><b>按房间推断抽样</b> —— 抽样为空说明设备表没读到，自采集也会失效。</item>
    /// </list>
    /// </remarks>
    [PatchFeature(
        "ClueSelfCheck",
        "启动自检：数据表就绪后在日志里输出一次诊断（未知字段通道自证、房主开关状态、按房间推断抽样、UI 建立情况）。只读不改行为。",
        defaultEnabled: true,
        side: FeatureSide.Both)]
    internal partial class SelfCheckFeature
    {
    }
}
