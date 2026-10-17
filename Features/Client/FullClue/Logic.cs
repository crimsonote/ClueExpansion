using System;
using System.Collections.Generic;
using ClueDetail.Core;
using Protocol;

namespace ClueDetail.Features.Client.FullClue
{
    /// <summary>
    /// 补扫的调度：每帧最多发一个请求，避免一帧几十个包把队列压崩。
    /// </summary>
    internal static class FullClueScan
    {
        private static readonly Queue<int> Pending = new Queue<int>();
        private static bool _done;

        public static void Tick()
        {
            try
            {
                var game = Managers.Game;
                if (game == null)
                {
                    return;
                }

                // 只在调查（侦探）阶段做：那之前设备上还没有线索可扫。
                if (game.State != EGameState.Detective || (game.IsAlive && !game.IsSpectator))
                {
                    _done = false;
                    Pending.Clear();
                    return;
                }

                if (_done)
                {
                    return;
                }

                if (Pending.Count == 0)
                {
                    Fill();
                    if (Pending.Count == 0)
                    {
                        _done = true;
                        return;
                    }
                }

                Send(Pending.Dequeue());
                if (Pending.Count == 0)
                {
                    _done = true;
                }
            }
            catch (Exception ex)
            {
                _done = true;
                global::ClueDetail.Core.Log.Exception(ex, "死者/旁观者补扫失败");
            }
        }

        private static void Fill()
        {
            var cache = Managers.Device?.Cache;
            if (cache == null)
            {
                return;
            }

            foreach (var pair in cache)
            {
                DeviceBase device = pair.Value;
                // Bubble == -1 是原版在 StartDetective 时给"有线索可扫描"的设备打的标记。
                if (device != null && device.Info != null && device.Info.Bubble == -1)
                {
                    Pending.Enqueue(device.ID);
                }
            }

            if (Pending.Count > 0)
            {
                global::ClueDetail.Core.Log.Info($"死者/旁观者补扫：待扫设备 {Pending.Count} 台。");
            }
        }

        private static void Send(int deviceId)
        {
            var network = Managers.Network;
            if (network == null || network.GameServer == null || deviceId == 0)
            {
                return;
            }

            // 先入队再发包：响应可能同帧就回来。
            ActivityMap.QueueScan(deviceId);
            network.GameServer.Send(new C_SCAN_DEVICE { DeviceId = deviceId });
        }
    }
}
