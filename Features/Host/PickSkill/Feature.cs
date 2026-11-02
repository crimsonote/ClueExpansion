using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.PickSkill
{
    /// <summary>
    /// 选人界面**两次选择**（实验性）：第一次选模型（原版），第二次选技能。
    /// </summary>
    /// <remarks>
    /// <para>机制依据（0.1.18c）：</para>
    /// <list type="bullet">
    /// <item><c>CharacterData.Skill</c> 只是角色的默认技能（:179742）；玩家实际技能由
    /// <c>Player.SkillComponent.Data</c> 决定（:177028），服务端可用
    /// <c>SkillComponent.AllocateSkill(CharacterData)</c> 换掉；</item>
    /// <item><c>AllocateSkill</c> 内的 <c>SendChangeSkill</c> **只发给本人**（:177052），
    /// 所以第二次选择天然匿名；</item>
    /// <item>第一次选完模型后，服务端再单发一条 <c>S_PICK_CHARACTER</c> 重新打开选人弹窗，
    /// 玩家第二次选的就是「技能」（取该角色的 <c>Skill</c>），不改模型、不广播。</item>
    /// </list>
    /// </remarks>
    [PatchFeature(
        "CluePickSkill",
        "选人界面两次选择：第一次选模型，第二次选技能（匿名，不广播，支持随机）。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class PickSkillFeature
    {
        /// <summary>第二次选择是否允许「随机」。</summary>
        public static ConfigEntry<bool> AllowRandom { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            AllowRandom = config.Bind(
                section,
                "AllowRandom",
                true,
                "第二次选择是否允许「随机」（与原版选人一样有两个随机选项）。");
        }

        internal static bool AllowRandomValue => AllowRandom == null || AllowRandom.Value;
    }
}
