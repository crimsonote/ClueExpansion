using System;
using System.Collections.Generic;
using System.Linq;
using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerSkillComponent = Server.Game.SkillComponent;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.PickSkill
{
    /// <summary>
    /// 两次选人的 Harmony 挂点。补丁类必须是 Feature 的**直接**嵌套类型（装载器只扫一层）。
    /// </summary>
    internal partial class PickSkillFeature
    {
        /// <summary>
        /// <c>GameRoom.PickCharacter</c>（0.1.18 :174189）：
        /// 模型阶段完全放行原版；技能阶段截住（原版会因 <c>_pickPlayers</c> 已含该玩家而静默忽略）。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.PickCharacter))]
        internal static class PickCharacterHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerGameRoom __instance, ServerGamePlayer player, int characterId)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return true;
                if (_broken) return true;

                EnsurePhase();
                if (_stage != Stage.Skill || __instance == null || __instance.State != EGameState.PickCharacter)
                {
                    return true;
                }

                if (player?.PublicInfo == null) return true;

                try
                {
                    if (ServerTimeManager.Instance != null && ServerTimeManager.Instance.StopWatch >= 40)
                    {
                        return false;
                    }

                    HandleSkillPick(player, characterId);
                    return false;
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：处理技能选择失败（放行原版）。");
                    return true;
                }
            }

            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance, ServerGamePlayer player, int characterId)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;
                if (_broken || _stage != Stage.Model || __instance == null
                    || __instance.State != EGameState.PickCharacter)
                {
                    return;
                }

                try
                {
                    OnModelPicked(__instance, player, characterId);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：记录模型失败。");
                }
            }
        }

        /// <summary>
        /// <c>GameRoom.CheckPickAllDone</c>（0.1.18 :174241）：
        /// 原版在“最后一个模型选完”时把 StopWatch 置 40；必须压住，否则技能阶段没时间。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "CheckPickAllDone")]
        internal static class CheckPickAllDoneHook
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return true;

                EnsurePhase();
                if (_broken) return true;
                return _stage == Stage.None;
            }
        }

        /// <summary>
        /// <c>GameRoom.PickCharacterTick</c>（0.1.18 :174091）：
        /// Prefix 记下这一拍开始时的 StopWatch；Postfix 在 40 秒那一拍进入技能阶段 / 做超时兜底。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.PickCharacterTick))]
        internal static class PickCharacterTickHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ref int __state)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>())
                {
                    __state = -1;
                    return true;
                }

                EnsurePhase();
                _timeZeroSuppressed = false;
                __state = ServerTimeManager.Instance != null ? ServerTimeManager.Instance.StopWatch : -1;
                return true;
            }

            [HarmonyPriority(Priority.Low)]
            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance, int __state)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;
                if (_broken) return;

                try
                {
                    if (_stage == Stage.Model)
                    {
                        EnsureModelsRecorded(__instance);
                        if (__state >= 40)
                        {
                            if (_timeZeroSuppressed)
                            {
                                StartSkillPhase();
                            }
                            else
                            {
                                global::ClueDetail.Core.Log.Error(
                                    "两次选人：40 秒那一拍的 S_TIME{0} 没能拦下，客户端按钮会被隐藏；跳过技能阶段，按原版收尾。");
                                Reset();
                            }
                        }
                        else
                        {
                            MaybeStartSkillPhase();
                        }
                    }
                    else if (_stage == Stage.Skill)
                    {
                        if (__state == 40)
                        {
                            AssignTimeoutSkills();
                        }
                        else if (__state >= 45)
                        {
                            RestoreUnassigned();
                            Reset();
                        }
                    }
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：技能阶段推进失败。");
                    try
                    {
                        RestoreUnassigned();
                    }
                    catch (Exception inner)
                    {
                        global::ClueDetail.Core.Log.Exception(inner, "两次选人：异常收尾恢复技能失败。");
                    }

                    Reset();
                }
            }
        }

        /// <summary>
        /// <c>GameRoom.Broadcast</c>（0.1.18 :174880）：模型阶段 40 秒那一拍的
        /// <c>S_TIME{Time = 0}</c> 会让客户端把提交/随机按钮隐藏（<c>SetTime</c> 的 <c>_pickClosed</c>），
        /// 之后无法再做技能选择。这里只在“即将进入技能阶段”的这一条上拦下，随后由 Postfix 广播新倒计时。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class BroadcastTimeZeroHook
        {
            [HarmonyPriority(Priority.First)]
            [HarmonyPrefix]
            private static bool Prefix(ServerGameRoom __instance, IMessage packet, int exceptId)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return true;
                if (_broken || _stage != Stage.Model) return true;
                if (__instance == null || __instance.State != EGameState.PickCharacter) return true;
                if (!(packet is S_TIME time) || time.Time != 0) return true;

                _timeZeroSuppressed = true;
                return false;
            }
        }

        /// <summary>
        /// <c>GameRoom.CancelPickCharacter</c>（0.1.18 :174249）：
        /// 模型阶段放行原版（并清本项目状态）；技能阶段一律拦下，避免把已选的模型/技能撤掉。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.CancelPickCharacter))]
        internal static class CancelPickCharacterHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerGameRoom __instance, ServerGamePlayer player)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return true;

                EnsurePhase();
                if (_broken || _stage != Stage.Skill) return true;

                if (player?.PublicInfo == null || __instance == null)
                {
                    return false;
                }

                int pid = player.PublicInfo.PlayerId;
                if (RandomSkillPlayers.Remove(pid))
                {
                    // 只取消“随机预留”，不碰已选的具体技能。
                    __instance.Broadcast(new S_CANCEL_PICK_CHARACTER
                    {
                        PlayerId = pid,
                        CharacterId = -2,
                    });
                    global::ClueDetail.Core.Log.Info(
                        $"两次选人：{NameOf(player)} 取消了随机，重新开放选择。");
                    MaybeComplete();
                    return false;
                }

                global::ClueDetail.Core.Log.Info(
                    $"两次选人：{NameOf(player)} 已选定具体技能，取消请求被忽略。");
                return false;
            }

            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance, ServerGamePlayer player)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;
                if (_broken || _stage != Stage.Model || player?.PublicInfo == null) return;

                try
                {
                    int pid = player.PublicInfo.PlayerId;
                    List<int> players = PickPlayers(__instance);
                    if (players != null && players.Contains(pid))
                    {
                        return; // 原版拒绝了这次取消，模型仍在
                    }

                    bool had = Model.Remove(pid);
                    had |= SkillSource.Remove(pid);
                    if (had)
                    {
                        global::ClueDetail.Core.Log.Info(
                            $"两次选人：{NameOf(player)} 已取消模型选择，等待重新选择。");
                    }

                    MaybeStartSkillPhase();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：处理取消选择失败。");
                }
            }
        }

        /// <summary><c>GameRoom.StartPick</c>（0.1.18 :174067）：新一轮选人开始，清空状态并进入模型阶段。</summary>
        [HarmonyPatch(typeof(ServerGameRoom), "StartPick")]
        internal static class StartPickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;

                EnsureReflection();
                Reset();
                if (!_broken)
                {
                    _stage = Stage.Model;
                }
            }
        }

        /// <summary>
        /// <c>GameRoom.ChangeGameState(EGameState, EGameState)</c>（0.1.18 :173057）：
        /// 离开选人阶段时先把没收尾的技能补齐，再清状态。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "ChangeGameState", new[] { typeof(EGameState), typeof(EGameState) })]
        internal static class ChangeGameStateHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState nextState)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;

                if (nextState == EGameState.PickCharacter)
                {
                    Reset();
                    if (!_broken)
                    {
                        _stage = Stage.Model;
                    }

                    return;
                }

                if (_stage == Stage.Skill)
                {
                    try
                    {
                        RestoreUnassigned();
                    }
                    catch (Exception ex)
                    {
                        global::ClueDetail.Core.Log.Exception(ex, "两次选人：离开选人阶段时补齐技能失败。");
                    }
                }

                Reset();
            }
        }

        /// <summary><c>TimeManager.ResetSurvival</c>（0.1.18 :181784）：换局清空。</summary>
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.ResetSurvival))]
        internal static class ResetSurvivalHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;
                Reset();
            }
        }

        /// <summary>
        /// <c>GameRoom.EnterPlayer</c>：技能阶段中途加入的人撤销其默认技能，
        /// 并阻止原版 40 秒强制分配再补一个默认技能。不做锁 UI 等额外防御。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "EnterPlayer", new[] { typeof(ServerGamePlayer) })]
        internal static class EnterPlayerHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance, ServerGamePlayer player)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;
                if (_broken || _stage != Stage.Skill) return;

                try
                {
                    OnPlayerEntered(__instance, player);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：处理中途加入失败。");
                }
            }
        }

        /// <summary>
        /// <c>SkillComponent.UseRuleBreaker</c>（0.1.18 :180672）：
        /// 原版按目标的 <c>CharacterId</c>（模型）复制默认技能；技能与模型解耦后要改成复制**实际技能**。
        /// </summary>
        [HarmonyPatch(typeof(ServerSkillComponent), "UseRuleBreaker", new[] { typeof(int) })]
        internal static class UseRuleBreakerHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerSkillComponent __instance, int targetId)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return true;

                try
                {
                    ServerGameRoom room = ServerGameRoom.Instance;
                    ServerGamePlayer owner = __instance?.Owner;
                    if (room == null || owner?.PublicInfo == null)
                    {
                        return true;
                    }

                    ServerGamePlayer target = room.Players.FirstOrDefault(
                        p => p?.PublicInfo != null && p.PublicInfo.PlayerId == targetId);
                    Data.SkillData actual = target?.SkillComponent?.Data;
                    if (actual == null || Managers.Data?.CharacterDic == null)
                    {
                        return true;
                    }

                    Data.CharacterData model = Managers.Data.CharacterDic.Values.FirstOrDefault(
                        x => x != null && x.DataId == target.PublicInfo.CharacterId);
                    if (model == null || model.Skill == actual.Type)
                    {
                        return true; // 未解耦，原版行为完全一致
                    }

                    Data.CharacterData carrier =
                        Managers.Data.CharacterDic.Values.FirstOrDefault(
                            x => x != null && x.Skill == actual.Type && x.Type != ECharacterType.Madeline)
                        ?? Managers.Data.CharacterDic.Values.FirstOrDefault(
                            x => x != null && x.Skill == actual.Type);
                    if (carrier == null)
                    {
                        return true;
                    }

                    room.BroadcastWorldSFX(ESoundType.BlackholeSfx, owner.PublicInfo.Pos);
                    __instance.AllocateSkill(carrier);
                    if (actual.Type == ESkillType.Catastrophe)
                    {
                        room.Broadcast(new S_NOTIFY_LUNA_ABILITY
                        {
                            PlayerId = owner.PublicInfo.PlayerId,
                        });
                    }

                    if (__instance.Data != null && __instance.Data.IsActive)
                    {
                        owner.CanUseSkill = true;
                    }

                    room.SendVFX(owner, EEffectType.ShadowPartnetVfx, targetId);
                    global::ClueDetail.Core.Log.Info(
                        $"两次选人：RuleBreaker 复制目标 {NameOf(target)} 的实际技能 {actual.Type}（原版会复制模型默认 {model.Skill}）。");
                    return false;
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：RuleBreaker 改取实际技能失败（放行原版）。");
                    return true;
                }
            }
        }
    }
}
