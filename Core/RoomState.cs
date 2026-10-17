using BepInEx.Configuration;

namespace ClueDetail.Core
{
    /// <summary>
    /// 本局是否启用「详细活动」的权威判定。
    /// </summary>
    /// <remarks>
    /// 三方状态必须分得清，否则会互相打架：
    /// <list type="bullet">
    /// <item><b>房主本机</b>：用本地配置 <see cref="HostBroadcast"/>。</item>
    /// <item><b>客户端 · 房主没装本模块</b>：收不到任何状态标记，退化为客户端自采集
    /// （<c>RemoteKnown == false</c> ⇒ 允许）。</item>
    /// <item><b>客户端 · 房主装了但关闭</b>：收到显式标记 0（<c>RemoteKnown &amp;&amp; !RemoteEnabled</c>）
    /// ⇒ 必须**同时关掉自采集**，否则客户端会把房主关掉的信息又自己补回来。</item>
    /// </list>
    /// 状态经 <c>PropositionInfo</c> 的未知字段（号 6）随线索包传播 —— 见 <see cref="UnknownFields"/>。
    /// 线索卡本来就是扫描之后才出现的，所以"拿到线索"时状态必然已经就绪。
    /// </remarks>
    internal static class RoomState
    {
        /// <summary>房主侧：本局是否下发详细活动（房主本机的权威值）。</summary>
        public static bool HostBroadcast { get; set; } = true;

        /// <summary>客户端侧：是否已收到房主的状态标记。</summary>
        public static bool RemoteKnown { get; set; }

        /// <summary>客户端侧：房主侧的开关值。</summary>
        public static bool RemoteEnabled { get; set; }

        public static bool IsHost
        {
            get
            {
                try
                {
                    return Managers.Host != null && Managers.Host.IsHost;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>当前这台机器是否应该显示详细活动。</summary>
        public static bool AllowDetail
        {
            get
            {
                if (IsHost)
                {
                    return HostBroadcast;
                }

                return !RemoteKnown || RemoteEnabled;
            }
        }

        /// <summary>换局 / 回大厅时清掉远端状态，避免上一局的开关残留。</summary>
        public static void ResetRemote()
        {
            RemoteKnown = false;
            RemoteEnabled = false;
        }

        public static void ApplyRemote(bool enabled)
        {
            RemoteKnown = true;
            RemoteEnabled = enabled;
        }
    }

    /// <summary>客户端自采集的开关（房主侧的开关走 <see cref="RoomState"/>）。</summary>
    internal static class SelfCapture
    {
        public static ConfigEntry<bool> Enabled { get; set; }

        public static bool On => Enabled == null || Enabled.Value;
    }
}
