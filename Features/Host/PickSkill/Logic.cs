using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.PickSkill
{
    /// <summary>
    /// 两次选人的全部运行期逻辑（补丁类只做入口检查与转发）。
    /// </summary>
    /// <remarks>
    /// 全局口径（2026-11-02 用户定）：
    /// <list type="number">
    /// <item>模型阶段与技能阶段完全独立；所有选人状态包一律 <c>Broadcast</c>，不存在“只对某个客户端”的伪造状态。</item>
    /// <item>进入技能阶段时：撤销所有人的默认技能 → 对每个已选模型广播 <c>S_CANCEL_PICK_CHARACTER</c>
    /// 完全重置选人界面 → 独立倒计时 → 先到先得。</item>
    /// <item>技能阶段里角色只是“技能载体”；任何人的模型角色都可以被别人选技能，
    /// 只要该技能当前没被占用。模型本人想保留默认技能，就得自己再选一次自己的模型。</item>
    /// </list>
    /// </remarks>
    internal partial class PickSkillFeature
    {
        private enum Stage
        {
            None,
            Model,
            Skill,
        }

        private static Stage _stage = Stage.None;

        /// <summary>本轮选定的模型角色（外观）。</summary>
        private static readonly Dictionary<int, int> Model = new Dictionary<int, int>();

        /// <summary>技能阶段选定的技能来源角色。</summary>
        private static readonly Dictionary<int, int> SkillSource = new Dictionary<int, int>();

        /// <summary>技能阶段按了“随机”的人：不占技能，等所有人决定后从剩余技能里分配。</summary>
        private static readonly HashSet<int> RandomSkillPlayers = new HashSet<int>();

        private static bool _broken;
        private static bool _loggedBroken;
        private static bool _completed;
        private static bool _timeZeroSuppressed;

        private static FieldInfo _pickPlayersField;
        private static FieldInfo _pickCharactersField;
        private static FieldInfo _randomPickPlayersField;
        private static MethodInfo _pickRandomOwnedMethod;
        private static bool _loggedRandomMethodMissing;

        // ══════════════════ 反射 ══════════════════

        private static void EnsureReflection()
        {
            if (_broken || _pickPlayersField != null)
            {
                return;
            }

            Type type = typeof(ServerGameRoom);
            _pickPlayersField = AccessTools.Field(type, "_pickPlayers");
            _pickCharactersField = AccessTools.Field(type, "_pickCharacters");
            _randomPickPlayersField = AccessTools.Field(type, "_randomPickPlayers");

            _pickRandomOwnedMethod = AccessTools.Method(
                type,
                "PickRandomOwnedCharacter",
                new[] { typeof(ServerGamePlayer), typeof(HashSet<int>) });
            if (_pickRandomOwnedMethod == null && !_loggedRandomMethodMissing)
            {
                _loggedRandomMethodMissing = true;
                global::ClueDetail.Core.Log.Warn(
                    "两次选人：找不到 GameRoom.PickRandomOwnedCharacter；随机模型仍等原版 40 秒揭晓。");
            }

            if (_pickPlayersField == null || _pickCharactersField == null || _randomPickPlayersField == null)
            {
                MarkBroken();
            }
        }

        private static void MarkBroken()
        {
            _broken = true;
            if (_loggedBroken)
            {
                return;
            }

            _loggedBroken = true;
            global::ClueDetail.Core.Log.Error(
                "两次选人：找不到 GameRoom._pickPlayers/_pickCharacters/_randomPickPlayers，功能退回原版。");
        }

        private static List<int> PickPlayers(ServerGameRoom room)
        {
            if (_broken || room == null || _pickPlayersField == null)
            {
                return null;
            }

            try
            {
                return _pickPlayersField.GetValue(room) as List<int>;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "两次选人：读取 _pickPlayers 失败。");
                MarkBroken();
                return null;
            }
        }

        private static List<int> PickCharacters(ServerGameRoom room)
        {
            if (_broken || room == null || _pickCharactersField == null)
            {
                return null;
            }

            try
            {
                return _pickCharactersField.GetValue(room) as List<int>;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "两次选人：读取 _pickCharacters 失败。");
                MarkBroken();
                return null;
            }
        }

        private static HashSet<int> RandomPickPlayers(ServerGameRoom room)
        {
            if (_broken || room == null || _randomPickPlayersField == null)
            {
                return null;
            }

            try
            {
                return _randomPickPlayersField.GetValue(room) as HashSet<int>;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "两次选人：读取 _randomPickPlayers 失败。");
                MarkBroken();
                return null;
            }
        }

        // ══════════════════ 阶段 ══════════════════

        /// <summary>首次进入选人阶段时补挂状态（启动 / 热开 / 中途接管都走这里）。</summary>
        private static void EnsurePhase()
        {
            if (_broken || _stage != Stage.None)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (room == null || tm == null || room.State != EGameState.PickCharacter)
            {
                return;
            }

            // 40 也允许：那一拍正好是模型阶段的最后；41+ 视为已经在收尾，不再接管。
            if (tm.StopWatch > 40)
            {
                return;
            }

            _stage = Stage.Model;
            SeedModelsFromOriginal(room);
        }

        private static void SeedModelsFromOriginal(ServerGameRoom room)
        {
            List<int> players = PickPlayers(room);
            List<int> characters = PickCharacters(room);
            if (players == null || characters == null)
            {
                return;
            }

            int count = Math.Min(players.Count, characters.Count);
            for (int i = 0; i < count; i++)
            {
                if (characters[i] != 0 && !Model.ContainsKey(players[i]))
                {
                    Model[players[i]] = characters[i];
                }
            }
        }

        /// <summary>把原版已经接受的模型补记进来（随机 / 40 秒强制分配走原版，不会进 Postfix）。</summary>
        private static void EnsureModelsRecorded(ServerGameRoom room)
        {
            if (room == null)
            {
                return;
            }

            List<int> players = PickPlayers(room);
            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (Model.ContainsKey(pid))
                {
                    continue;
                }

                if (players != null && players.Contains(pid))
                {
                    int model = player.PublicInfo.CharacterId;
                    if (model != 0)
                    {
                        Model[pid] = model;
                    }
                }
            }
        }

        private static bool AllModelsConcrete(ServerGameRoom room)
        {
            if (room == null || room.Players.Count == 0)
            {
                return false;
            }

            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null || !Model.ContainsKey(player.PublicInfo.PlayerId))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 原版对随机（<c>-2</c>）只是登记 <c>_randomPickPlayers</c>，真正分配要等 40 秒的
        /// <c>PickCharacterTick</c>。这里在原版“所有玩家都已决定（具体或随机）”时提前调用原版的
        /// <c>PickRandomOwnedCharacter</c> 把随机模型揭晓，从而立即进入技能阶段。
        /// </summary>
        private static bool TryResolveRandomModels(ServerGameRoom room)
        {
            List<int> players = PickPlayers(room);
            List<int> characters = PickCharacters(room);
            HashSet<int> random = RandomPickPlayers(room);
            if (players == null || characters == null || random == null)
            {
                return false;
            }

            // 还有既没选具体模型、也没按随机的人：交给原版 40 秒强制分配。
            if (players.Count + random.Count < room.Players.Count)
            {
                return false;
            }

            if (random.Count == 0)
            {
                return true;
            }

            if (_pickRandomOwnedMethod == null)
            {
                return false;
            }

            HashSet<int> taken = new HashSet<int>(characters);
            bool resolved = false;
            foreach (int pid in random.ToList())
            {
                if (players.Contains(pid))
                {
                    random.Remove(pid); // 已选具体模型，随机的旧登记作废
                    continue;
                }

                ServerGamePlayer player = room.Players.FirstOrDefault(
                    p => p?.PublicInfo != null && p.PublicInfo.PlayerId == pid);
                if (player == null)
                {
                    random.Remove(pid);
                    continue;
                }

                int model;
                try
                {
                    model = (int)_pickRandomOwnedMethod.Invoke(null, new object[] { player, taken });
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：提前揭晓随机模型失败，等原版 40 秒。");
                    return false;
                }

                if (model == 0)
                {
                    return false;
                }

                taken.Add(model);
                players.Add(pid);
                characters.Add(model);
                player.CharacterId = model;
                Model[pid] = model;
                room.Broadcast(new S_PICK_CHARACTER
                {
                    PlayerId = pid,
                    CharacterId = model,
                });
                random.Remove(pid);
                resolved = true;
            }

            if (resolved)
            {
                global::ClueDetail.Core.Log.Info(
                    "两次选人：所有玩家都已决定（含随机），已提前揭晓随机模型并准备进入技能阶段。");
            }

            return random.Count == 0;
        }

        private static void MaybeStartSkillPhase()
        {
            if (_broken || _stage != Stage.Model)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            EnsureModelsRecorded(room);
            if (!TryResolveRandomModels(room))
            {
                return;
            }

            if (!AllModelsConcrete(room))
            {
                return;
            }

            StartSkillPhase();
        }

        /// <summary>
        /// 进入技能阶段：撤销默认技能 → 完全重置选人界面 → 独立倒计时。
        /// </summary>
        /// <remarks>
        /// “完全重置”使用全局广播：对每个已选模型的玩家广播一条
        /// <c>S_CANCEL_PICK_CHARACTER { PlayerId = 该玩家, CharacterId = 模型 }</c>。
        /// 每个客户端收到的是同一序列：所有头像被清空、所有模型角色重新点亮、
        /// 每个客户端在自己的那条包上把提交状态复位。没有任何“只对某个客户端”的伪造状态。
        /// </remarks>
        private static void StartSkillPhase()
        {
            if (_broken || _stage != Stage.Model)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (room == null || tm == null)
            {
                return;
            }

            EnsureModelsRecorded(room);
            _stage = Stage.Skill;
            _completed = false;
            RandomSkillPlayers.Clear();

            // 1) 全局撤销默认技能：模型阶段分配的技能只是“暂存”，技能阶段全部回到可竞争状态。
            foreach (ServerGamePlayer player in room.Players)
            {
                try
                {
                    player?.SkillComponent?.DeallocateSkill();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(
                        ex, $"两次选人：撤销 {NameOf(player)} 的默认技能失败。");
                }
            }

            // 2) 完全重置选人界面（全局同一序列）。
            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (!Model.TryGetValue(pid, out int model) || model == 0)
                {
                    continue;
                }

                try
                {
                    room.Broadcast(new S_CANCEL_PICK_CHARACTER
                    {
                        PlayerId = pid,
                        CharacterId = model,
                    });
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：重置选人界面失败。");
                }
            }

            // 3) 技能阶段的独立倒计时：把原版 40 秒的显示值改写成 SkillPickSeconds。
            int seconds = SkillPickSecondsValue;
            tm.SetStopWatch(40 - seconds);
            try
            {
                room.Broadcast(new S_TIME
                {
                    Time = seconds,
                    Remain = (int)tm.RemainTime,
                    SurvivalTime = tm.SurviveTime,
                });
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "两次选人：广播技能阶段倒计时失败。");
            }

            global::ClueDetail.Core.Log.Info(
                $"两次选人：模型阶段结束，进入技能阶段（{seconds} 秒）；" +
                $"已释放全部默认技能并重置选人界面，先选先得。");
        }

        /// <summary>
        /// 全员都“已决定”（具体技能 或 随机预留）时完成阶段；
        /// 若有随机预留，先从剩余技能里分配，再进入原版 5 秒倒计时。
        /// </summary>
        private static void MaybeComplete()
        {
            if (_broken || _stage != Stage.Skill || _completed)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            bool allSpecific = true;
            bool allDecided = true;
            foreach (ServerGamePlayer player in room.Players)
            {
                if (!IsParticipant(player) || player?.PublicInfo == null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (SkillSource.ContainsKey(pid))
                {
                    continue;
                }

                allSpecific = false;
                if (!RandomSkillPlayers.Contains(pid))
                {
                    allDecided = false;
                }
            }

            if (!allSpecific)
            {
                if (!allDecided)
                {
                    return;
                }

                ResolveRandomSkills(room);

                // 分配后必须每个人都真的有技能，才允许完成。
                foreach (ServerGamePlayer player in room.Players)
                {
                    if (!IsParticipant(player) || player?.PublicInfo == null)
                    {
                        continue;
                    }

                    if (!SkillSource.ContainsKey(player.PublicInfo.PlayerId))
                    {
                        return;
                    }
                }
            }

            FinalizeSkillPhase(room);
        }

        /// <summary>把随机预留的人从“剩余技能”里分配掉；不占用别人的选择。</summary>
        private static void ResolveRandomSkills(ServerGameRoom room)
        {
            if (RandomSkillPlayers.Count == 0)
            {
                return;
            }

            int count = RandomSkillPlayers.Count;
            foreach (int pid in RandomSkillPlayers.ToList())
            {
                RandomSkillPlayers.Remove(pid);
                if (SkillSource.ContainsKey(pid))
                {
                    continue;
                }

                ServerGamePlayer player = room.Players.FirstOrDefault(
                    p => p?.PublicInfo != null && p.PublicInfo.PlayerId == pid);
                if (player == null || !IsParticipant(player))
                {
                    continue;
                }

                ApplyRandomSkill(player);
            }

            global::ClueDetail.Core.Log.Info(
                $"两次选人：所有人已决定，{count} 名随机玩家已从剩余技能里分配。");
        }

        private static void FinalizeSkillPhase(ServerGameRoom room)
        {
            _completed = true;
            AssignNonParticipants(room);
            ServerTimeManager.Instance?.SetStopWatch(40);
            global::ClueDetail.Core.Log.Info(
                "两次选人：所有参与者已完成技能选择，进入原版 5 秒倒计时。");
        }

        /// <summary>技能阶段到 40 秒：未选的参与者从剩余技能里随机兜底；非参与者一并分配。</summary>
        private static void AssignTimeoutSkills()
        {
            if (_broken || _stage != Stage.Skill)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            int count = 0;
            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (SkillSource.ContainsKey(pid) || !IsParticipant(player))
                {
                    continue;
                }

                ApplyRandomSkill(player);
                count++;
            }

            RandomSkillPlayers.Clear();
            AssignNonParticipants(room);
            global::ClueDetail.Core.Log.Info(
                $"两次选人：技能阶段到点，{count} 名参与者未选，已从剩余技能随机兜底。");
            MaybeComplete();
        }

        /// <summary>旁观者 / 假人不阻塞阶段；阶段结束时给它们补一个剩余技能即可。</summary>
        private static void AssignNonParticipants(ServerGameRoom room)
        {
            if (room == null)
            {
                return;
            }

            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null || IsParticipant(player))
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (SkillSource.ContainsKey(pid))
                {
                    continue;
                }

                int source = PickRandomFreeAny();
                if (source == 0)
                {
                    int model = Model.TryGetValue(pid, out int m) ? m : 0;
                    if (model != 0 && TryChar(model, out Data.CharacterData data) && !IsSkillTaken(data.Skill))
                    {
                        source = model;
                    }
                }

                if (source != 0 && AssignSkill(player, source))
                {
                    global::ClueDetail.Core.Log.Info(
                        $"两次选人：非参与者 {NameOf(player)} 已补技能 {source}（不阻塞阶段）。");
                }
            }
        }

        // ══════════════════ 选人处理 ══════════════════

        /// <summary>模型阶段：原版接受一次模型选择后记录；全员就绪则进入技能阶段。</summary>
        private static void OnModelPicked(ServerGameRoom room, ServerGamePlayer player, int characterId)
        {
            if (room == null || player?.PublicInfo == null)
            {
                return;
            }

            int pid = player.PublicInfo.PlayerId;

            // 随机（-2）原版只登记、不分配模型；这里触发 MaybeStart 去提前揭晓。
            if (characterId == -2)
            {
                MaybeStartSkillPhase();
                return;
            }

            if (!Model.ContainsKey(pid))
            {
                List<int> players = PickPlayers(room);
                if (players != null && players.Contains(pid))
                {
                    int model = player.PublicInfo.CharacterId;
                    if (model != 0)
                    {
                        Model[pid] = model;
                    }
                }
            }

            MaybeStartSkillPhase();
        }

        /// <summary>技能阶段：处理一次 <c>C_PICK_CHARACTER</c>。</summary>
        private static void HandleSkillPick(ServerGamePlayer player, int characterId)
        {
            if (player?.PublicInfo == null)
            {
                return;
            }

            int pid = player.PublicInfo.PlayerId;
            if (SkillSource.ContainsKey(pid))
            {
                return;
            }

            if (!IsParticipant(player))
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            if (RandomSkillPlayers.Contains(pid))
            {
                // 已按随机；想改具体技能必须先取消随机（和模型阶段一致）。
                return;
            }

            if (characterId == -2)
            {
                if (!AllowRandomValue)
                {
                    global::ClueDetail.Core.Log.Info(
                        $"两次选人：{NameOf(player)} 按了随机，但 [CluePickSkill] AllowRandom = false，忽略。");
                    return;
                }

                if (RandomSkillPlayers.Add(pid))
                {
                    // 只表示“我最后再选”，不占用任何技能；别人仍可任选。
                    room.Broadcast(new S_PICK_CHARACTER
                    {
                        PlayerId = pid,
                        CharacterId = -2,
                    });
                    global::ClueDetail.Core.Log.Info(
                        $"两次选人：{NameOf(player)} 选择了随机（不占技能，等所有人决定后分配）。");
                }

                MaybeComplete();
                return;
            }

            if (!TryChar(characterId, out Data.CharacterData data)
                || data.Type == ECharacterType.Madeline
                || !HasSkillData(data.Skill)
                || IsSkillTaken(data.Skill))
            {
                global::ClueDetail.Core.Log.Info(
                    $"两次选人：{NameOf(player)} 选择的 {characterId} 不可用（不存在/不可选/技能缺失/已被占用），改为随机。");
                ApplyRandomSkill(player);
                MaybeComplete();
                return;
            }

            if (AssignSkill(player, characterId))
            {
                MaybeComplete();
                return;
            }

            global::ClueDetail.Core.Log.Warn(
                $"两次选人：{NameOf(player)} 应用 {characterId} 失败，改为随机。");
            ApplyRandomSkill(player);
            MaybeComplete();
        }

        /// <summary>给参与者随机一个未占用的技能；没有空闲时回退到自己的模型默认。</summary>
        private static void ApplyRandomSkill(ServerGamePlayer player)
        {
            if (player?.PublicInfo == null)
            {
                return;
            }

            int pid = player.PublicInfo.PlayerId;
            int source = PickRandomSkillSource(player);
            if (source != 0 && AssignSkill(player, source))
            {
                return;
            }

            int model = Model.TryGetValue(pid, out int m) ? m : 0;
            if (model != 0 && AssignSkill(player, model))
            {
                return;
            }

            // 最后兜底：记录一个占位，避免 MaybeComplete 永远等下去。
            SkillSource[pid] = model;
            SendModelAndMaybeFree(player);
            global::ClueDetail.Core.Log.Warn(
                $"两次选人：{NameOf(player)} 没有可用技能，保留模型默认（{model}）。");
        }

        /// <summary>真正把某个角色对应的技能装到玩家身上；成功才记录占用。</summary>
        private static bool AssignSkill(ServerGamePlayer player, int characterId)
        {
            if (player?.PublicInfo == null || player.SkillComponent == null)
            {
                return false;
            }

            int pid = player.PublicInfo.PlayerId;
            if (SkillSource.ContainsKey(pid))
            {
                return false;
            }

            if (!TryChar(characterId, out Data.CharacterData data)
                || data.Type == ECharacterType.Madeline
                || IsSkillTaken(data.Skill)
                || !HasSkillData(data.Skill))
            {
                return false;
            }

            try
            {
                player.SkillComponent.DeallocateSkill();
                player.SkillComponent.AllocateSkill(data);
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(
                    ex, $"两次选人：{NameOf(player)} 应用技能 {characterId} 失败。");
                return false;
            }

            SkillSource[pid] = characterId;
            BroadcastSkillTaken(data.Skill);
            SendModelAndMaybeFree(player);
            global::ClueDetail.Core.Log.Info(
                $"两次选人：{NameOf(player)} 技能 → {data.Skill}（来源角色 {characterId}，" +
                $"模型 {ModelOf(pid)}）。");
            return true;
        }

        // ══════════════════ 候选 / 校验 ══════════════════

        private static bool IsParticipant(ServerGamePlayer player)
        {
            return player != null && !player.IsDummy && !player.IsSpectator;
        }

        private static bool TryChar(int characterId, out Data.CharacterData data)
        {
            data = null;
            if (characterId == 0 || Managers.Data?.CharacterDic == null)
            {
                return false;
            }

            return Managers.Data.CharacterDic.TryGetValue(characterId, out data) && data != null;
        }

        private static bool HasSkillData(ESkillType skill)
        {
            if (Managers.Data == null || Managers.Data.SkillDic == null)
            {
                return false;
            }

            return Managers.Data.SkillDic.Values.Any(x => x != null && x.Type == skill);
        }

        /// <summary>某个技能此刻是否已被某个玩家占用（技能阶段只看“已选定”，不看模型默认）。</summary>
        private static bool IsSkillTaken(ESkillType skill)
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return false;
            }

            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (SkillSource.TryGetValue(pid, out int source)
                    && TryChar(source, out Data.CharacterData data)
                    && data.Skill == skill)
                {
                    return true;
                }

                // 中途加入者可能在技能阶段被原版分配了默认技能；也计入，避免重复。
                if (player.SkillComponent?.Data != null && player.SkillComponent.Data.Type == skill)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>参与者随机候选：已拥有 → 默认拥有 → 全部；只排除已占用技能/Medelin。</summary>
        private static int PickRandomSkillSource(ServerGamePlayer player)
        {
            int source = PickRandomFrom(player?.OwnedCharacterIds, player);
            if (source != 0)
            {
                return source;
            }

            source = PickRandomFrom(Define.DEFAULT_OWNED_CHARACTER_IDS, player);
            if (source != 0)
            {
                return source;
            }

            if (Managers.Data == null || Managers.Data.CharacterDic == null)
            {
                return 0;
            }

            return PickRandomFrom(Managers.Data.CharacterDic.Keys, player);
        }

        private static int PickRandomFrom(IEnumerable<int> ids, ServerGamePlayer player)
        {
            if (ids == null)
            {
                return 0;
            }

            List<int> list = new List<int>();
            foreach (int id in ids)
            {
                if (list.Contains(id) || !TryChar(id, out Data.CharacterData data))
                {
                    continue;
                }

                if (data.Type == ECharacterType.Madeline
                    || !HasSkillData(data.Skill)
                    || IsSkillTaken(data.Skill))
                {
                    continue;
                }

                list.Add(id);
            }

            if (list.Count == 0)
            {
                return 0;
            }

            return list[Util.GetRandomNumber(0, list.Count)];
        }

        /// <summary>非参与者兜底：任意一个未占用的技能来源角色。</summary>
        private static int PickRandomFreeAny()
        {
            if (Managers.Data == null || Managers.Data.CharacterDic == null)
            {
                return 0;
            }

            List<int> list = new List<int>();
            foreach (Data.CharacterData data in Managers.Data.CharacterDic.Values)
            {
                if (data == null || data.Type == ECharacterType.Madeline
                    || !HasSkillData(data.Skill) || IsSkillTaken(data.Skill))
                {
                    continue;
                }

                list.Add(data.DataId);
            }

            if (list.Count == 0)
            {
                return 0;
            }

            return list[Util.GetRandomNumber(0, list.Count)];
        }

        // ══════════════════ 广播 ══════════════════

        /// <summary>
        /// 技能被占用：对**所有**角色里技能相同的那些角色广播 <c>S_PICK_CHARACTER { PlayerId = 0 }</c>。
        /// 这是全局匿名广播：所有人看到同一状态，只知道“该技能没了”，不知道是谁选的。
        /// </summary>
        private static void BroadcastSkillTaken(ESkillType skill)
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || Managers.Data == null || Managers.Data.CharacterDic == null)
            {
                return;
            }

            try
            {
                foreach (Data.CharacterData data in Managers.Data.CharacterDic.Values)
                {
                    if (data == null || data.Skill != skill)
                    {
                        continue;
                    }

                    room.Broadcast(new S_PICK_CHARACTER
                    {
                        PlayerId = 0,
                        CharacterId = data.DataId,
                    });
                }
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "两次选人：广播技能占用失败。");
            }
        }

        /// <summary>
        /// 回显玩家自己的模型：恢复所有人看到的头像、把该玩家自己的提交状态锁回 true。
        /// 若模型对应的技能此刻空闲，再广播一条 <c>S_CANCEL { PlayerId = 0 }</c> 把模型角色重新点亮。
        /// 全部是全局广播。
        /// </summary>
        private static void SendModelAndMaybeFree(ServerGamePlayer player)
        {
            if (player?.PublicInfo == null)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            int pid = player.PublicInfo.PlayerId;
            if (!Model.TryGetValue(pid, out int model) || model == 0)
            {
                return;
            }

            try
            {
                room.Broadcast(new S_PICK_CHARACTER
                {
                    PlayerId = pid,
                    CharacterId = model,
                });

                if (TryChar(model, out Data.CharacterData data) && !IsSkillTaken(data.Skill))
                {
                    room.Broadcast(new S_CANCEL_PICK_CHARACTER
                    {
                        PlayerId = 0,
                        CharacterId = model,
                    });
                }
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "两次选人：回显模型失败。");
            }
        }

        /// <summary>热关时把界面锁回“人人已提交模型”，让原版阶段能正常收尾。</summary>
        private static void LockAllWithModel()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                if (!Model.TryGetValue(pid, out int model) || model == 0)
                {
                    continue;
                }

                try
                {
                    room.Broadcast(new S_PICK_CHARACTER
                    {
                        PlayerId = pid,
                        CharacterId = model,
                    });
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "两次选人：锁定选人界面失败。");
                }
            }
        }

        // ══════════════════ 收尾 ══════════════════

        /// <summary>给还没技能的人补上：优先自己的模型默认，其次剩余技能，最后模型默认（极端兜底）。</summary>
        private static void RestoreUnassigned()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            foreach (ServerGamePlayer player in room.Players)
            {
                if (player?.PublicInfo == null || player.SkillComponent == null
                    || player.SkillComponent.Data != null)
                {
                    continue;
                }

                int pid = player.PublicInfo.PlayerId;
                int model = Model.TryGetValue(pid, out int m) ? m : 0;

                if (TryAssignForExit(player, model, false))
                {
                    continue;
                }

                int source = PickRandomFreeAny();
                if (source != 0 && TryAssignForExit(player, source, false))
                {
                    continue;
                }

                TryAssignForExit(player, model, true);
            }
        }

        private static bool TryAssignForExit(ServerGamePlayer player, int characterId, bool allowTaken)
        {
            if (player?.PublicInfo == null || player.SkillComponent == null)
            {
                return false;
            }

            if (characterId == 0 || !TryChar(characterId, out Data.CharacterData data)
                || data.Type == ECharacterType.Madeline
                || !HasSkillData(data.Skill))
            {
                return false;
            }

            if (!allowTaken && IsSkillTaken(data.Skill))
            {
                return false;
            }

            try
            {
                player.SkillComponent.DeallocateSkill();
                player.SkillComponent.AllocateSkill(data);
                SkillSource[player.PublicInfo.PlayerId] = characterId;
                return true;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(
                    ex, $"两次选人：收尾恢复 {NameOf(player)} 的技能 {characterId} 失败。");
                return false;
            }
        }

        /// <summary>换局 / 离开选人阶段：清空本轮状态。</summary>
        internal static void Reset()
        {
            _stage = Stage.None;
            _completed = false;
            _timeZeroSuppressed = false;
            Model.Clear();
            SkillSource.Clear();
            RandomSkillPlayers.Clear();
        }

        /// <summary>
        /// 技能阶段中途加入：只撤销它的默认技能并把它放进原版的选人列表，
        /// 防止原版 40 秒强制分配在“已释放全部默认技能”之后又补一个默认技能造成重复。
        /// 不做锁 UI / 收尾窗口补技能等额外防御。
        /// </summary>
        private static void OnPlayerEntered(ServerGameRoom room, ServerGamePlayer player)
        {
            if (room == null || player?.PublicInfo == null)
            {
                return;
            }

            int pid = player.PublicInfo.PlayerId;
            int model = player.PublicInfo.CharacterId;

            List<int> players = PickPlayers(room);
            List<int> characters = PickCharacters(room);
            if (model != 0)
            {
                Model[pid] = model;
                if (characters != null && !characters.Contains(model))
                {
                    characters.Add(model);
                }
            }

            // 即便模型是 0 也把 pid 放进 _pickPlayers：原版 40 秒强制分配只看这个列表。
            if (players != null && !players.Contains(pid))
            {
                players.Add(pid);
            }

            try
            {
                player.SkillComponent?.DeallocateSkill();
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, $"两次选人：撤销中途加入者 {NameOf(player)} 的技能失败。");
            }

            global::ClueDetail.Core.Log.Info(
                $"两次选人：{NameOf(player)} 在技能阶段加入，已撤销默认技能并阻止原版强制分配。");
        }

        // ══════════════════ 小工具 ══════════════════

        private static int ModelOf(int pid)
        {
            return Model.TryGetValue(pid, out int model) ? model : 0;
        }

        private static string NameOf(ServerGamePlayer player)
        {
            if (player == null)
            {
                return "?";
            }

            string name = player.Name;
            return string.IsNullOrEmpty(name) ? $"#{player.PublicInfo?.PlayerId ?? 0}" : name;
        }
    }
}
