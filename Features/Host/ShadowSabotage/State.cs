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

        /// <summary>
        /// **共享起始进度**（0..1）—— 语义等同原版电箱的 <c>StateList[2]</c>：
        /// 谁中断就把**他自己跑到的进度**并进来（取最大值），之后进来的人从这个值接着跑。
        /// </summary>
        public float BaseProgress;

        /// <summary>已废弃：改为按会话计算（见 <see cref="Sessions"/>）。保留只为兼容旧引用。</summary>
        public float Progress;

        /// <summary>
        /// 进行中的会话：<c>PlayerId → 开始时刻</c>（<c>TimeManager.SurviveTime</c> 秒）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 2026-10-20 改（用户口径）：**多人可以同时修，谁先跑满谁完成**。
        /// 对齐原版电箱的行为 —— 客户端 <c>Fusebox.Interact</c>（:3203）每人各自
        /// <c>StartCasting(10f)</c>，服务端 <c>SaveRepairTick</c>（:165608）只接受更大的 tick。
        /// </para>
        /// <para>
        /// 旧的单会话锁（<c>SessionPid</c>）会把第二个人刚挂上的进度条顶掉 ——
        /// 那是「接力修」，不是「人人独立进度条、先到先得」。
        /// </para>
        /// </remarks>
        public readonly Dictionary<int, float> Sessions = new Dictionary<int, float>();

        /// <summary>每个会话上次下发的进度条刻度（<c>PlayerId → 0..24</c>），变了才发包。</summary>
        public readonly Dictionary<int, int> LastBar = new Dictionary<int, int>();

        /// <summary>
        /// 破坏完成的时刻（<c>TimeManager.SurviveTime</c> 秒）。
        /// 时间污染冻结成的就是它 —— 所以必须在 <see cref="Finish"/> 里、状态翻转的同一刻记下。
        /// </summary>
        public int BrokenAt;

        /// <summary>断电箭头是否已发（P3）。</summary>
        public bool ArrowSent;

        /// <summary>
        /// 修复完成的时刻（<c>TimeManager.SurviveTime</c> 秒）；0 = **还没修复**（窗口向右敞开）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="BrokenAt"/> 一起定义**污染窗口** <c>[BrokenAt, RepairedAt)</c>（2026-10-20 重构）。
        /// 这是判断"某条记录是否产生于污染期间"的唯一依据：
        /// <list type="bullet">
        /// <item>破坏期间记录 ⇒ 落在窗口内；</item>
        /// <item>修复后再记录 ⇒ 在窗口外（<c>Time &gt;= RepairedAt</c>），不会被误判；</item>
        /// <item>修复前记录 ⇒ 仍在窗口内 ⇒ 这就是"残留"，符合"污染结束不恢复，直到其恢复正常"。</item>
        /// </list>
        /// ⚠️ 判据**不看阶段、不看 Broken** —— 历史事实不随阶段切换或修复而改变；
        /// 早前用 <c>PollutionOn</c>（内含 <c>State == Survive</c>）去判断，导致
        /// "离开 Survive 那一刻进调查时镜子一台都没熄灭"（判据恒为 false）。
        /// </remarks>
        public int RepairedAt;

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

        /// <summary>
        /// 是否有任一破坏点正处于「已破坏」态。
        /// </summary>
        /// <remarks>
        /// 2026-10-18 口径：**破坏期间一切其他破坏都要被挡住** —— 另一个破坏点、以及原版电闸
        /// （含电闸图钉/平板条目）。这条判据同时被两处使用：
        /// <list type="bullet">
        /// <item><see cref="ShadowSabotageFeature.IsEligible"/>：未破坏的点在有人破坏期间拿不到假主人 ⇒ 按 E 无提示；</item>
        /// <item><see cref="ShadowSabotageFeature.OnWindowStart"/>：原版重派发撞上我们仍在破坏时，把窗口再按回去。</item>
        /// </list>
        /// </remarks>
        public static bool AnyBroken
        {
            get
            {
                for (int i = 0; i < All.Length; i++)
                {
                    if (All[i].Broken)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static BreakerState Of(BreakerKind kind)
        {
            return kind == BreakerKind.Identify ? Identify : Clock;
        }

        public static BreakerState Of(BreakerDevice device)
        {
            return device == null ? null : Of(device.Kind);
        }

        /// <summary>把两个点恢复到「装机时」的初始状态（插件加载 / 换局）。</summary>
        /// <summary>
        /// 镜子「脸轮换」的节流时间戳（服务端 <c>realtimeSinceStartup</c>）。
        /// </summary>
        /// <remarks>
        /// 只影响**展示**：身份破坏期间，每个玩家看到的镜子里的人每秒换一次（按玩家 id 升序轮转）。
        /// 真值（服务端 <c>DeviceInfo.StateList</c>）一个字都不改 —— 与「假主人」同一套单发做法。
        /// </remarks>
        internal static float LastMirrorCycle;

        /// <summary>
        /// 镜子「污染残留」表：设备 id → 污染时的真实记录时间。
        /// </summary>
        /// <remarks>
        /// 为什么需要它：污染结束**不恢复**（用户口径），镜子上的假脸留在客户端等待真实更新；
        /// 而"还要不要继续轮换"不能看污染开关（那样一修复就停），只能看**这条记录是否仍是残留**。
        /// 判据：真值 <c>StateList[2]</c> 与表里记的时间**仍然相同** ⇒ 没人重新用过 ⇒ 仍是残留。
        /// 一旦被真实使用覆盖（时间变了）或镜子被熄灭（<c>StateList[1] == 0</c>）⇒ 从表里移除。
        /// </remarks>
        internal static readonly global::System.Collections.Generic.Dictionary<int, int> MirrorResidual =
            new global::System.Collections.Generic.Dictionary<int, int>();

        public static void Reset()
        {
            LastMirrorCycle = 0f;
            MirrorResidual.Clear();
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
            state.Sessions.Clear();
            state.LastBar.Clear();
            state.ArrowSent = false;
            state.BrokenAt = 0;
            state.RepairedAt = 0;
            state.Faked.Clear();
            state.LastResend.Clear();
        }
    }
}
