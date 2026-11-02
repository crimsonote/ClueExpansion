using System;
using System.Collections.Generic;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.PickSkill
{
    internal partial class PickSkillFeature
    {
        /// <summary>已经选过模型的玩家（第二次调用 = 选技能）。</summary>
        private static readonly HashSet<int> ModelPicked = new HashSet<int>();

        /// <summary>第二次选择里还需要放行的「随机」标记。</summary>
        private static readonly HashSet<int> SkillPicked = new HashSet<int>();

        /// <summary>已被占用的技能（全局共享：同一技能只能一个人选）。</summary>
        private static readonly HashSet<ESkillType> OccupiedSkills = new HashSet<ESkillType>();

        /// <summary>每局重置（ResetSurvival 时调用）。</summary>
        internal static void Reset()
        {
            ModelPicked.Clear();
            SkillPicked.Clear();
        }

        /// <summary>
        /// 第一次：放行原版（设置模型）；Postfix 里记标记并重新打开选人弹窗。
        /// 第二次：拦截原版（不改模型、不广播），只把选中的角色技能装到本人身上。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.PickCharacter))]
        internal static class PickHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerGameRoom __instance, ServerGamePlayer player, int characterId)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return true;
                if (player == null || player.PublicInfo == null) return true;

                try
                {
                    int pid = player.PublicInfo.PlayerId;

                    // 第二次调用 = 选技能
                    if (ModelPicked.Contains(pid) && !SkillPicked.Contains(pid))
                    {
                        ApplySkill(player, characterId);
                        return false;       // 不改模型、不广播
                    }

                    return true;            // 第一次：原版
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：选人拦截失败（放行）。");
                    return true;
                }
            }

            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance, ServerGamePlayer player, int characterId)
            {
                if (!FeatureLoader.Enabled<PickSkillFeature>()) return;
                if (player == null || player.PublicInfo == null) return;

                try
                {
                    int pid = player.PublicInfo.PlayerId;
                    if (ModelPicked.Contains(pid) || SkillPicked.Contains(pid))
                    {
                        return;
                    }

                    // 原版已接受这次模型选择 ⇒ 记下来，再打开一次选人弹窗
                    if (characterId == 0)
                    {
                        return;
                    }

                    ModelPicked.Add(pid);
                    player.Session.Send(new S_PICK_CHARACTER
                    {
                        CharacterId = characterId,
                        PlayerId = pid,
                    });

                    global::ClueDetail.Core.Log.Info(
                        $"两次选人：{player.Name} 已选模型 {characterId}，再次打开选人界面选技能。");
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：重开选人界面失败。");
                }
            }
        }

        private static void ApplySkill(ServerGamePlayer player, int characterId)
        {
            SkillPicked.Add(player.PublicInfo.PlayerId);

            Data.CharacterData data = null;
            if (characterId == -2)
            {
                if (!AllowRandomValue)
                {
                    global::ClueDetail.Core.Log.Info("两次选人：随机选技能被关闭，忽略。");
                    return;
                }

                var list = new List<Data.CharacterData>();
                foreach (var c in Managers.Data.CharacterDic.Values)
                {
                    if (!OccupiedSkills.Contains(c.Skill))
                    {
                        list.Add(c);
                    }
                }

                if (list.Count > 0)
                {
                    data = list[new global::System.Random().Next(list.Count)];
                }
            }
            else
            {
                foreach (var c in Managers.Data.CharacterDic.Values)
                {
                    if (c != null && c.DataId == characterId)
                    {
                        data = c;
                        break;
                    }
                }
            }

            if (data == null)
            {
                global::ClueDetail.Core.Log.Warn($"两次选人：找不到角色 {characterId}，技能不变。");
                return;
            }

            if (OccupiedSkills.Contains(data.Skill))
            {
                // 全局共享：同一技能只能一个人选，拒绝并重开弹窗。
                global::ClueDetail.Core.Log.Info($"两次选人：{player.Name} 选择的 {data.Skill} 已被占用。");
                SkillPicked.Remove(player.PublicInfo.PlayerId);
                player.Session.Send(new S_PICK_CHARACTER { CharacterId = 0, PlayerId = player.PublicInfo.PlayerId });
                return;
            }

            OccupiedSkills.Add(data.Skill);
            player.SkillComponent.AllocateSkill(data);

            // 与原版选人一致的广播：让其他人的选人弹窗也能看到「这个技能被占了」。
            // 匿名口径：PlayerId 用 0（占位「某人」），不暴露是谁。
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room != null)
            {
                room.Broadcast(new S_PICK_CHARACTER
                {
                    CharacterId = data.DataId,
                    PlayerId = 0,
                });
            }
            global::ClueDetail.Core.Log.Info(
                $"两次选人：{player.Name} 的技能已切到 {data.Skill}（匿名）。");
        }

        /// <summary>每局开局清空（选人发生在生存阶段之前，所以清在这里最稳）。</summary>
        [HarmonyPatch(typeof(ServerGameRoom), "StartSurvive")]
        internal static class ResetHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Reset();
            }
        }
    }
}
