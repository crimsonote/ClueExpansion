using ClueDetail.Core;
using HarmonyLib;
using GamePlayer = Server.Game.Player;
using ServerDeviceManager = Server.Game.DeviceManager;

namespace ClueDetail.Features.Host.SpectatorScan
{
    internal partial class SpectatorScanFeature
    {
        /// <summary>
        /// 放行旁观者扫描。Prefix 返回 false 表示"原方法不执行"，由我们调用同一份原版方法。
        /// </summary>
        /// <remarks>
        /// 只在"本机是房主且本模块开着"时介入。非旁观者直接放行走原版，零额外开销。
        /// </remarks>
        [HarmonyPatch(typeof(ServerDeviceManager), nameof(ServerDeviceManager.Scan))]
        internal static class ScanHook
        {
            private static bool Prefix(ServerDeviceManager __instance, GamePlayer player, int id)
            {
                if (!FeatureLoader.Enabled<SpectatorScanFeature>()) return true;
                if (__instance == null || player == null || !player.IsSpectator)
                {
                    return true;
                }

                if (!RoomState.HostBroadcast)
                {
                    return true;
                }

                // Player.IsSpectator 是可写属性（0.1.18 反编译 :29694）。
                player.IsSpectator = false;
                try
                {
                    __instance.Scan(player, id);
                }
                finally
                {
                    player.IsSpectator = true;
                }

                return false;
            }
        }
    }
}
