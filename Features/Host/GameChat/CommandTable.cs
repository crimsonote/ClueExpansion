using System;
using System.Collections.Generic;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.GameChat
{
    /// <summary>
    /// 命令从哪条频道来。一个命令可以声明多条（例如 <c>/f</c> 要在平板聊天里给提示、
    /// 在发信机频道里真正执行）。
    /// </summary>
    [Flags]
    internal enum ChatChannelMask
    {
        None = 0,
        Public = 1,     // NormalChat（平板聊天）
        Device = 2,     // DeviceChat（发信机）
        Secret = 4,     // SecretChat（密聊）
    }

    /// <summary>命令可用的游戏阶段（位掩码）。</summary>
    [Flags]
    internal enum ChatPhaseMask
    {
        None = 0,
        Lobby = 1,
        PickCharacter = 2,
        Survive = 4,
        Detective = 8,
        Trial = 16,
        Any = Lobby | PickCharacter | Survive | Detective | Trial,
    }

    /// <summary>
    /// 命令归属的阵营。不属于该阵营的玩家输入这个名字时**不认领**（就像本模块没注册过），
    /// 原版/其它模块照常处理。
    /// </summary>
    internal enum ChatSide
    {
        Any = 0,

        /// <summary>只有黑方（<c>EPlayerColor.Black</c>）与黑幕（<c>Dark</c> / <c>MasterMind</c>）。</summary>
        Black = 1,

        /// <summary>只有白方（非黑、非黑幕）。</summary>
        White = 2,
    }

    /// <summary>
    /// 一条聊天命令的**声明**：名字、别名、说明、频道、阶段、是否吞包，以及执行委托。
    /// </summary>
    /// <remarks>
    /// 本模块的命令分发统一走这张表（<see cref="ChatCommandTable"/>）：
    /// 表负责「命令是否存在 / 从哪条频道来 / 是否该由本模块处理 / 处理完是否吞掉」，
    /// 具体执行仍是各命令自己的方法。新增命令只在这里注册一行。
    /// </remarks>
    internal sealed class ChatCommandDef
    {
        /// <summary>主名（小写）。</summary>
        public string Name;

        /// <summary>别名（小写，含单字母）。</summary>
        public string[] Aliases = new string[0];

        /// <summary>帮助里显示的用法（不含斜杠）。</summary>
        public string Usage;

        /// <summary>一句话说明。</summary>
        public string Description;

        /// <summary>除主行之外额外显示在帮助里的行（例如 <c>/c help</c>）。</summary>
        public string[] ExtraHelp = new string[0];

        /// <summary>接受哪些频道。</summary>
        public ChatChannelMask Channels = ChatChannelMask.Public;

        /// <summary>
        /// 归属阵营。不属于该阵营的玩家输入这个名字时**不认领**（放行，像没注册过）。
        /// </summary>
        public ChatSide Side = ChatSide.Any;

        /// <summary>哪些阶段出现在帮助里（执行门禁仍由命令自己判，保持既有行为）。</summary>
        public ChatPhaseMask HelpPhases = ChatPhaseMask.Any;

        /// <summary>
        /// 处理完是否吞掉消息（<c>return false</c>）。
        /// <c>/help</c> 为 false —— 只报到，让链上其它模块继续报自己的帮助。
        /// </summary>
        public bool Swallow = true;

        /// <summary>执行委托。参数是本次命令的上下文（房间、玩家、频道、设备号、参数）。</summary>
        public Action<ChatContext> Handler;
    }

    /// <summary>一次命令执行的上下文。</summary>
    internal sealed class ChatContext
    {
        public ServerGameRoom Room;
        public ServerGamePlayer Player;
        public ChatChannelMask Channel;
        public int DeviceId;
        public string Args;

        /// <summary>按来源频道回执一条（Public → 普通聊天；Device → 发信机频道）。</summary>
        public void Reply(string text)
        {
            Reply(text, null);
        }

        /// <summary>按来源频道回执多行（3 行/气泡由底层统一打包）。</summary>
        public void Reply(string single, List<string> lines)
        {
            GameChatFeature.ReplyByChannel(Room, Player, Channel, DeviceId, single, lines);
        }
    }

    /// <summary>
    /// 聊天命令注册表 + 分发器（本模块的**唯一**命令入口）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 表里只放「声明」，不放业务。分发时的职责边界：
    /// <list type="number">
    /// <item><b>归属</b>：名字（含别名）在表里 ⇒ 这是本模块的命令，否则原样放行；</item>
    /// <item><b>频道</b>：命令声明的频道不包含消息来源 ⇒ 放行（不抢别人的频道）；</item>
    /// <item><b>吞包</b>：命令声明 <see cref="ChatCommandDef.Swallow"/> 才吞，<c>/help</c> 不吞；</item>
    /// <item><b>执行</b>：委托给命令自己的 <see cref="ChatCommandDef.Handler"/>。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 阶段门禁**暂时留在各命令内部**：现有行为（<c>/say</c> 大厅/会议/调查、<c>/clue</c> 仅会议、
    /// <c>/f</c> 仅生存）都是命令自己判的，表里的 <see cref="ChatCommandDef.HelpPhases"/>
    /// 只用于帮助列表过滤。等这个表抽成共享库时再把门禁上移。
    /// </para>
    /// </remarks>
    internal static class ChatCommandTable
    {
        private static readonly List<ChatCommandDef> Defs = new List<ChatCommandDef>();

        private static readonly Dictionary<string, ChatCommandDef> ByName =
            new Dictionary<string, ChatCommandDef>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已注册的命令（注册顺序，用于帮助列表）。</summary>
        internal static IReadOnlyList<ChatCommandDef> All
        {
            get { return Defs; }
        }

        /// <summary>清空（热重载 / 重新注册时用）。</summary>
        internal static void Clear()
        {
            Defs.Clear();
            ByName.Clear();
        }

        /// <summary>注册一条命令；名字或别名冲突时抛异常（启动期就该暴露）。</summary>
        internal static void Register(ChatCommandDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Name))
            {
                throw new ArgumentException("命令声明缺少主名。");
            }

            Claim(def.Name, def);
            for (int i = 0; i < def.Aliases.Length; i++)
            {
                if (!string.IsNullOrEmpty(def.Aliases[i]))
                {
                    Claim(def.Aliases[i], def);
                }
            }

            Defs.Add(def);
        }

        private static void Claim(string name, ChatCommandDef def)
        {
            ChatCommandDef existing;
            if (ByName.TryGetValue(name, out existing) && !ReferenceEquals(existing, def))
            {
                throw new InvalidOperationException(
                    "聊天命令名/别名冲突：" + name + "（" + existing.Name + " vs " + def.Name + "）");
            }

            ByName[name] = def;
        }

        /// <summary>按名字或别名查找。</summary>
        internal static bool TryFind(string name, out ChatCommandDef def)
        {
            def = null;
            return !string.IsNullOrEmpty(name) && ByName.TryGetValue(name.Trim(), out def);
        }

        /// <summary>该命令是否接受这条频道。</summary>
        internal static bool Accepts(ChatCommandDef def, ChatChannelMask channel)
        {
            return (def.Channels & channel) != 0;
        }

        /// <summary>
        /// 该玩家是否属于命令声明的阵营。
        /// </summary>
        /// <remarks>
        /// ⚠️ 判据按 AGENTS.md §4.9：<c>Black</c> = 黑（杀手）、<c>Dark</c> = 黑幕。
        /// 「黑方全体」⇒ <c>Black || Dark</c>（黑幕另有 <c>MasterMind</c> 引用可判）。
        /// 非本阵营 ⇒ 命令**不认领**，调用方应放行原消息（如同没注册）。
        /// </remarks>
        internal static bool SideAllows(ChatCommandDef def, ServerGameRoom room, ServerGamePlayer player)
        {
            if (def == null || player == null)
            {
                return false;
            }

            switch (def.Side)
            {
                case ChatSide.Any:
                    return true;

                case ChatSide.Black:
                    return room != null && room.MasterMind == player
                        || player.Color == EPlayerColor.Dark
                        || player.Color == EPlayerColor.Black;

                case ChatSide.White:
                    return player.Color != EPlayerColor.Dark && player.Color != EPlayerColor.Black;

                default:
                    return false;
            }
        }

        /// <summary>把消息来源转成渠道位。</summary>
        internal static ChatChannelMask ChannelOf(EChatType type)
        {
            switch (type)
            {
                case EChatType.NormalChat: return ChatChannelMask.Public;
                case EChatType.DeviceChat: return ChatChannelMask.Device;
                case EChatType.SecretChat: return ChatChannelMask.Secret;
                default: return ChatChannelMask.None;
            }
        }

        /// <summary>把游戏阶段转成帮助用的阶段位。</summary>
        internal static ChatPhaseMask PhaseOf(EGameState state)
        {
            switch (state)
            {
                case EGameState.Lobby: return ChatPhaseMask.Lobby;
                case EGameState.PickCharacter: return ChatPhaseMask.PickCharacter;
                case EGameState.Survive: return ChatPhaseMask.Survive;
                case EGameState.Detective: return ChatPhaseMask.Detective;
                case EGameState.Trial: return ChatPhaseMask.Trial;
                default: return ChatPhaseMask.None;
            }
        }

        /// <summary>
        /// 按阶段生成帮助行。格式与原来的 <c>AvailableCommands</c> 一致：
        /// <c>/c [条件...]  搜索线索（= /clue）</c>。
        /// </summary>
        internal static string[] HelpLines(ServerGameRoom room)
        {
            ChatPhaseMask phase = room == null ? ChatPhaseMask.Any : PhaseOf(room.State);
            var lines = new List<string>();

            for (int i = 0; i < Defs.Count; i++)
            {
                ChatCommandDef def = Defs[i];
                if (phase != ChatPhaseMask.Any && (def.HelpPhases & phase) == 0)
                {
                    continue;
                }

                string shortName = def.Aliases.Length > 0 ? def.Aliases[0] : def.Name;
                string line = "/" + shortName;
                if (!string.IsNullOrEmpty(def.Usage))
                {
                    line += " " + def.Usage;
                }

                if (!string.IsNullOrEmpty(def.Description))
                {
                    line += "  " + def.Description;
                }

                lines.Add(line);

                for (int k = 0; k < def.ExtraHelp.Length; k++)
                {
                    lines.Add(def.ExtraHelp[k]);
                }
            }

            return lines.ToArray();
        }

        /// <summary>
        /// 分发一条已确认归属本模块的命令。
        /// </summary>
        /// <returns><c>true</c> = 已处理（调用方应吞掉原消息）。</returns>
        internal static bool Dispatch(
            ServerGameRoom room, ServerGamePlayer player, ChatCommandDef def,
            string args, ChatChannelMask channel, int deviceId)
        {
            if (def == null || def.Handler == null)
            {
                return false;
            }

            var ctx = new ChatContext
            {
                Room = room,
                Player = player,
                Channel = channel,
                DeviceId = deviceId,
                Args = args ?? string.Empty,
            };

            def.Handler(ctx);
            return def.Swallow;
        }
    }
}
