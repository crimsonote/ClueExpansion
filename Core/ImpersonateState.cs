using UnityEngine;
using ServerGamePlayer = Server.Game.Player;

namespace ClueDetail.Core
{
    /// <summary>
    /// 调试用「冒名」状态：一段时间内，把**某个玩家**的使用行为记到**另一个玩家**头上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用途（2026-10-20 用户需求）：假人不能真的操作设备，所以没法自然产生
    /// 「**别人**完成的线索」。开启本状态后，被指定的「源玩家」的任何设备使用
    /// 都会被记成「目标玩家」，配合身份破坏点即可验证
    /// 「别人搜集到的线索在身份破坏期间是否显示为『某人』」。
    /// </para>
    /// <para>
    /// ⚠️ **只由命令桥（<c>cd_impersonate</c>）控制，不暴露为游戏内聊天命令**。
    /// 挂在 <c>Device.RecordLastUsingPlayer</c> 的 Prefix 上，换掉 <c>player</c> 参数。
    /// </para>
    /// <para>
    /// ⚠️ 2026-10-20 修正：**目标玩家在 <see cref="Start"/> 时就解析并持有引用**。
    /// 旧写法每次替换时遍历 <c>room.Players</c> 现找，找不到就静默跳过 ——
    /// 实机表现是「命令回执说开了，但日志里一条 <c>[2 / …]</c> 都没有」，
    /// 完全看不出是「没开」还是「开了但目标 ID 对不上」。
    /// 现在目标 ID 必须真实存在，否则命令直接失败并回执现有 ID 列表。
    /// </para>
    /// </remarks>
    internal static class ImpersonateState
    {
        /// <summary>默认持续时长（秒）。</summary>
        internal const float DefaultSeconds = 60f;

        /// <summary>被替换的玩家 ID；0 = 未开启。</summary>
        private static int _sourceId;

        /// <summary>替换成谁（ID）。</summary>
        private static int _targetId;

        /// <summary>结束时刻（<c>Time.realtimeSinceStartup</c> 秒）。</summary>
        private static float _untilRealtime;

        /// <summary>
        /// 目标玩家对象（<see cref="Start"/> 时解析，之后直接用，不再遍历 <c>room.Players</c>）。
        /// </summary>
        internal static ServerGamePlayer Target { get; private set; }

        /// <summary>当前是否处于生效期（过期或目标失效会自动关闭）。</summary>
        internal static bool Active
        {
            get
            {
                if (_sourceId == 0 || _targetId == 0)
                {
                    return false;
                }

                if (Time.realtimeSinceStartup > _untilRealtime)
                {
                    Stop();
                    return false;
                }

                // 目标掉线 / 被移除 ⇒ 立刻失效，别让它半开半关。
                if (Target == null || Target.PublicInfo == null)
                {
                    Stop();
                    return false;
                }

                return true;
            }
        }

        internal static int SourceId => _sourceId;

        internal static int TargetId => _targetId;

        /// <summary>剩余秒数（状态回执用）。</summary>
        internal static float RemainSeconds
        {
            get
            {
                if (!Active)
                {
                    return 0f;
                }

                float left = _untilRealtime - Time.realtimeSinceStartup;
                return left < 0f ? 0f : left;
            }
        }

        /// <summary>这个玩家此刻是否应当被冒名。</summary>
        internal static bool AppliesTo(int playerId)
        {
            return Active && playerId == _sourceId && playerId != _targetId;
        }

        /// <summary>开启；<paramref name="target"/> 必须已经解析好（调用方负责找不到就拒绝）。</summary>
        internal static void Start(int sourceId, int targetId, float seconds, ServerGamePlayer target)
        {
            _sourceId = sourceId;
            _targetId = targetId;
            Target = target;
            _untilRealtime = Time.realtimeSinceStartup + (seconds > 0f ? seconds : DefaultSeconds);
        }

        internal static void Stop()
        {
            _sourceId = 0;
            _targetId = 0;
            Target = null;
            _untilRealtime = 0f;
        }
    }
}
