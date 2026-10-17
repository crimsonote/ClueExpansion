using ClueDetail.Core;

namespace ClueDetail.Features.Host.SpectatorScan
{
    /// <summary>
    /// 房主侧：放行旁观者的设备扫描请求。
    /// </summary>
    /// <remarks>
    /// 0.1.18 的服务端 <c>DeviceManager.Scan</c> 第一行就是 <c>if (!player.IsSpectator)</c> ——
    /// 旁观者永远扫不到设备，也就永远拿不到线索。本功能在**房主启用本模块**时临时摘掉这个守卫，
    /// 让旁观者也能补齐线索；房主关闭本模块（或没装）时完全维持原版行为。
    /// <para>
    /// 实现刻意不碰 <c>DeviceManager.Objects</c>（可见性不确定，且复刻一行筛选容易随版本漂移），
    /// 而是把 <c>Player.IsSpectator</c> 临时置 false 后**调用原方法**，再在 finally 里恢复 ——
    /// 逻辑仍然只有原版那一份。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueSpectatorScan",
        "房主侧：放行旁观者的设备扫描请求（原版会挡 IsSpectator）。放行后旁观者也能拿到全部线索并建立线索与设备的关联；房主关闭本模块时自动回到原版行为。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static partial class SpectatorScanFeature
    {
    }
}
