using System;
using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.PickSkill
{
    /// <summary>
    /// 选人界面两次选择：第一次选模型（原版），第二次统一选技能。
    /// </summary>
    /// <remarks>
    /// <para>两阶段完全独立、全程全局同步（所有状态包都走 <c>GameRoom.Broadcast</c>）：</para>
    /// <list type="number">
    /// <item>模型阶段完全走原版（40 秒、随机 <c>-2</c> 在 40 秒揭晓、强制分配）；本功能只记录模型，
    /// 并在全员选完后进入技能阶段。</item>
    /// <item>进入技能阶段时：服务端撤销所有人的默认技能（<c>SkillComponent.DeallocateSkill</c>），
    /// 再对每个已选模型广播 <c>S_CANCEL_PICK_CHARACTER</c>，把选人界面完全重置
    /// （清空所有头像、复位所有人的提交状态、点亮所有模型角色）；随后全部技能对所有人先到先得。</item>
    /// <item>技能选择匿名（服务端广播 <c>PlayerId = 0</c>），模型不变；
    /// 选择自己模型的技能即“保持默认”。</item>
    /// </list>
    /// <para>纯服务端：只复用原版 <c>S_PICK_CHARACTER</c> / <c>S_CANCEL_PICK_CHARACTER</c> / <c>S_TIME</c>，
    /// 不需要客户端安装本模块，也不向个别客户端发送“只对它成立”的伪造状态。</para>
    /// </remarks>
    [PatchFeature(
        "CluePickSkill",
        "选人界面两次选择：第一次选模型（原版），全员选完后统一进入第二次选技能。\n" +
        "模型只决定外观；进入技能阶段时服务端释放所有人的模型默认技能，选人界面完全重置，\n" +
        "所有技能对所有人先到先得。想保留模型默认技能就在第二次选自己的模型；\n" +
        "若该技能已被别人选走，则按随机从剩余技能里补一个。第二次选择匿名（不暴露是谁）。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class PickSkillFeature
    {
        /// <summary>第二次选择是否允许“随机”按钮；第一次（模型）随机始终走原版。</summary>
        public static ConfigEntry<bool> AllowRandom { get; private set; }

        /// <summary>第二次选择（技能阶段）的独立倒计时，秒。</summary>
        public static ConfigEntry<int> SkillPickSeconds { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            AllowRandom = config.Bind(
                section,
                "AllowRandom",
                true,
                "第二次选择（技能阶段）是否允许“随机”按钮。\n" +
                "true = 与原版选人一样随机一个未占用技能；\n" +
                "false = 按随机键无效（仍可手动选角色，超时按剩余技能随机兜底）。");

            SkillPickSeconds = config.Bind(
                section,
                "SkillPickSeconds",
                20,
                new ConfigDescription(
                    "第二次选择（技能阶段）的独立倒计时，秒。\n" +
                    "模型阶段结束后统一进入技能阶段，并重新给这么多秒；\n" +
                    "到点仍未选的按剩余技能随机兜底。允许 5–40。",
                    new AcceptableValueRange<int>(5, 40)));
        }

        internal static bool AllowRandomValue => AllowRandom == null || AllowRandom.Value;

        internal static int SkillPickSecondsValue =>
            SkillPickSeconds == null ? 20 : Math.Clamp(SkillPickSeconds.Value, 5, 40);

        /// <summary>启动时自检反射目标；与开关状态无关。</summary>
        public static void OnLoaded()
        {
            EnsureReflection();
            if (!_broken)
            {
                global::ClueDetail.Core.Log.Info(
                    "两次选人：反射目标齐全（_pickPlayers/_pickCharacters/_randomPickPlayers）。");
            }
        }

        /// <summary>热开：若正处在选人阶段，补挂状态。</summary>
        public static void OnEnabled()
        {
            EnsureReflection();
            EnsurePhase();
            MaybeStartSkillPhase();
        }

        /// <summary>热关：把技能阶段收拾回原版可继续的状态。</summary>
        public static void OnDisabled()
        {
            if (_stage == Stage.Skill)
            {
                RestoreUnassigned();
                LockAllWithModel();
            }

            Reset();
        }
    }
}
