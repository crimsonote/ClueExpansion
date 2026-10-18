using System.Collections.Generic;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>两个破坏点的种类。</summary>
    internal enum BreakerKind
    {
        /// <summary>身份识别组件（科学室最左）：破坏期间新线索一律记成「某人」。</summary>
        Identify = 0,

        /// <summary>时钟记录组件（电脑室刀架下）：破坏期间新线索的时间冻结在破坏那一刻。</summary>
        Clock = 1,
    }

    /// <summary>
    /// 一个破坏点的**运行期状态**。
    /// </summary>
    /// <remarks>
    /// 进度、CD、伪装名单一律放这里，**绝不能塞进 <c>DeviceInfo.StateList</c>**：
    /// 客户端 <c>Summon.RefreshState</c>（0.1.18 :4139）断言 <c>StateList.Count == 2</c>，
    /// 第三槽会让整段刷新被跳过（连小熊贴图都不更新）。
    /// 于是这 2 个槽的用途被钉死：<c>[0]</c> = 0 正常 / 2 已破坏，<c>[1]</c> = 假主人位。
    /// </remarks>
    internal sealed class BreakerState
    {
        public BreakerKind Kind;

        /// <summary>服务端设备 ID（见 <see cref="ShadowSabotageFeature.IdentifyDeviceId"/>）。</summary>
        public int DeviceId;

        /// <summary>是否已破坏。<c>false</c> 时可以破坏，<c>true</c> 时可以修复。</summary>
        public bool Broken;

        /// <summary>
        /// 当前是否处于「可破坏窗口」—— 就是与电闸共用的那条 CD。
        /// 电闸重派发（<c>StartFuseboxSabotage</c>）时置 true，被破坏后置 false；
        /// **修复不会让它立刻变回 true**。
        /// </summary>
        public bool CanBreak = true;

        /// <summary>已完成进度（0..1）。打断时保留，破坏/修复完成后清零。</summary>
        public float BaseProgress;

        /// <summary>当前进度（0..1，含本次会话）。仅用于收尾时写回 <see cref="BaseProgress"/>。</summary>
        public float Progress;

        /// <summary>正在操作的玩家 PlayerId；0 = 没有进行中的会话。</summary>
        public int SessionPid;

        /// <summary>本次会话的开始时刻（<c>TimeManager.SurviveTime</c> 秒）。</summary>
        public float SessionStartAt;

        /// <summary>进度条是否已挂在该玩家屏幕上。</summary>
        public bool BarOn;

        /// <summary>上次下发给进度条的刻度（变了才发包）。</summary>
        public int LastBar = -1;

        /// <summary>
        /// 破坏完成的时刻（<c>TimeManager.SurviveTime</c> 秒）。
        /// 时间污染冻结成的就是它 —— 所以必须在 <see cref="Finish"/> 里、状态翻转的同一刻记下。
        /// </summary>
        public int BrokenAt;

        /// <summary>断电箭头是否已发（P3）。</summary>
        public bool ArrowSent;

        /// <summary>可调查线索是否已注入（P4）。</summary>
        public bool ClueInjected;

        /// <summary>当前被伪装成「他的小熊」的玩家 PlayerId 集合。</summary>
        public readonly HashSet<int> Faked = new HashSet<int>();

        /// <summary>PlayerId → 上次重发假主人的时刻（秒），用于节流。</summary>
        public readonly Dictionary<int, float> LastResend = new Dictionary<int, float>();
    }

    /// <summary>两个破坏点的状态容器。</summary>
    internal static class BreakerStates
    {
        public static readonly BreakerState Identify = new BreakerState
        {
            Kind = BreakerKind.Identify,
            DeviceId = ShadowSabotageFeature.IdentifyDeviceId,
        };

        public static readonly BreakerState Clock = new BreakerState
        {
            Kind = BreakerKind.Clock,
            DeviceId = ShadowSabotageFeature.ClockDeviceId,
        };

        /// <summary>全部破坏点（顺序固定，遍历用）。</summary>
        public static readonly BreakerState[] All = { Identify, Clock };

        public static BreakerState Of(BreakerKind kind)
        {
            return kind == BreakerKind.Identify ? Identify : Clock;
        }

        public static BreakerState Of(BreakerDevice device)
        {
            return device == null ? null : Of(device.Kind);
        }

        /// <summary>把两个点恢复到「装机时」的初始状态（插件加载 / 换局）。</summary>
        public static void Reset()
        {
            for (int i = 0; i < All.Length; i++)
            {
                ResetOne(All[i]);
            }
        }

        /// <summary>只做状态清理，不动设备实体（实体由 <see cref="ShadowSabotageFeature"/> 负责创建/销毁）。</summary>
        public static void ResetOne(BreakerState state)
        {
            if (state == null)
            {
                return;
            }

            state.Broken = false;
            state.CanBreak = true;
            state.BaseProgress = 0f;
            state.Progress = 0f;
            state.SessionPid = 0;
            state.SessionStartAt = 0f;
            state.BarOn = false;
            state.LastBar = -1;
            state.ArrowSent = false;
            state.ClueInjected = false;
            state.BrokenAt = 0;
            state.Faked.Clear();
            state.LastResend.Clear();
        }
    }
}
