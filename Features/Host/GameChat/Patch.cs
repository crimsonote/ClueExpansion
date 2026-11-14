using DummyClient;
using HarmonyLib;
using ClueDetail.Core;
using Protocol;
using ClueFakeCommand = ClueDetail.Features.Host.ClueFake.ClueFakeCommand;
using ServerGameHostPacketHandler = Server.Game.HostPacketHandler;
using ServerGameHostPeerSession = Server.Game.HostPeerSession;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.GameChat
{
    internal partial class GameChatFeature
    {
        /// <summary>
        /// 拦截玩家的聊天包，按 <see cref="ChatCommandTable"/> 认领本模块的命令。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ <c>[HarmonyPriority(Priority.First)]</c> 是**必需**的：捉迷藏的游戏内命令也挂在这里
        /// （默认 <c>Normal</c> 优先级）且会吞掉 <c>/help</c>；我们必须先报到，它才能继续报它的帮助。
        /// </para>
        /// <para>
        /// 三条频道都读：<c>NormalChat</c>（平板聊天）、<c>DeviceChat</c>（公开发信机）、
        /// <c>SecretChat</c>（黑方秘密终端）。命令只在注册时声明过的频道生效；
        /// 未命中（名字不在表里 / 频道不匹配）一律放行，不抢别人的命令。
        /// </para>
        /// <para>
        /// ⚠️ 命令解析在 <c>room.Push(...)</c> 里做（与捉迷藏、原版一致）：那是在主线程上执行，
        /// 可以安全地碰 Unity / 玩家对象；本 Prefix 只做"要不要拦"的判断。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ServerGameHostPacketHandler), "Handle_C_CHAT_MESSAGE")]
        internal static class ChatCommandHook
        {
            private static bool Prefix(IPacketSink session, Packet packet)
            {
                if (!FeatureLoader.Enabled<GameChatFeature>()) return true;

                try
                {
                    if (!(packet?.Pkt is C_CHAT_MESSAGE msg))
                    {
                        return true;
                    }

                    ChatChannelMask channel = ChatCommandTable.ChannelOf(msg.Type);
                    if (channel == ChatChannelMask.None)
                    {
                        return true;
                    }

                    return HandleChannel(session, msg, channel);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "游戏内命令：拦截失败（交还原版）。");
                    return true;
                }
            }

            /// <summary>
            /// 三条频道共用：命令查表分发；非命令只把「黑方秘密终端里进行中的会话」当答案吞掉。
            /// </summary>
            private static bool HandleChannel(
                IPacketSink session, C_CHAT_MESSAGE msg, ChatChannelMask channel)
            {
                if (!(session is ServerGameHostPeerSession peer))
                {
                    return true;
                }

                ServerGamePlayer player = peer.Player;
                ServerGameRoom room = ServerGameRoom.Instance;
                if (player == null || room == null)
                {
                    return true;
                }

                string text = (msg.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    return true;
                }

                if (text[0] == '/')
                {
                    string[] parts = text.Substring(1)
                        .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        return true;
                    }

                    string name = parts[0].ToLowerInvariant();
                    ChatCommandDef def;
                    if (!ChatCommandTable.TryFind(name, out def))
                    {
                        return true;                  // ① 只认领自己的名字 ⇒ 放行给别人
                    }

                    // ② 功能被关掉时**不认领**：/f 就像没注册过（秘密终端里原版会静默丢弃，
                    // 平板聊天里原版照常处理），而不是把玩家的输入静默吞掉。
                    if (def.Name == "fake"
                        && (!FeatureLoader.Enabled<ClueDetail.Features.Host.ClueFake.ClueFakeFeature>()
                            || (!ClueDetail.Features.Host.ClueFake.ClueFakeFeature.BlackEnabled.Value
                                && player.Color == EPlayerColor.Black)))
                    {
                        return true;
                    }

                    if (!ChatCommandTable.Accepts(def, channel))
                    {
                        return true;                  // 频道不匹配 ⇒ 不抢别人的频道
                    }

                    // 阵营不匹配 ⇒ 不认领（例如白方输入 /f：如同本模块没注册）。
                    if (!ChatCommandTable.SideAllows(def, room, player))
                    {
                        return true;
                    }

                    string arg = parts.Length > 1
                        ? string.Join(" ", parts, 1, parts.Length - 1)
                        : string.Empty;

                    room.Push(delegate { ChatCommandTable.Dispatch(room, player, def, arg, channel, msg.DeviceId); });

                    // ③ /help 只报到不吞（Swallow=false）；其余认领了就吞。
                    return !def.Swallow;
                }

                // 非命令：只有黑方秘密终端里「进行中的 /f 会话」才当答案吞掉。
                // 普通聊天 / 公开发信机留言一律放行，不能影响别的模块与正常留言。
                if (channel == ChatChannelMask.Secret
                    && player.PublicInfo != null
                    && ClueFakeCommand.HasSession(player.PublicInfo.PlayerId))
                {
                    room.Push(delegate
                    {
                        ClueFakeCommand.HandleAnswer(room, player, msg.DeviceId, text);
                    });
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// <c>/help</c> 的**广播兜底**：如果链上没有任何模块吞掉它（例如捉迷藏没启用/没开该频道），
        /// 原版会把 <c>/help</c> 当成普通聊天广播给全服。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么挂这里：我们的 <c>/help</c> 必须 <c>return true</c>（让别的模块也能报到），
        /// 那就无法自己吞；但原版最后一定会经过 <c>RelayNormalChat</c>（0.1.18 :177812）才广播 ——
        /// 在那里拦一道即可：**有人吞就轮不到它（天然无副作用），没人吞就由我们兜底吞掉**。
        /// </para>
        /// <para>
        /// ⚠️ <b>本类必须**直接**嵌套在 <see cref="GameChatFeature"/> 下，绝不能放进
        /// <see cref="ChatCommandHook"/></b>：装载器 <c>FeatureLoader.MountPatches</c> 只认
        /// <c>DeclaringType == featureType</c> 的**一层**嵌套（见 AGENTS.md §11.7），
        /// 二级嵌套的补丁类**一个都挂不上**，而且启动日志仍然报「失败 0」。
        /// 2026-10-20 实测：本类曾写成 <c>ChatCommandHook.HelpBroadcastGuard</c>，
        /// 于是 <c>/help</c> 一直照常广播到公聊 —— 看着像"写了但没生效"，实际是根本没挂载。
        /// </para>
        /// <para>
        /// ⚠️ 只拦**完全等于 <c>/help</c>**（允许前后空白），其它 <c>/</c> 开头的文本一律放行 ——
        /// 不给别的模块的命令添麻烦。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameHostPacketHandler), "RelayNormalChat")]
        internal static class HelpBroadcastGuard
        {
            private static bool Prefix(string text)
            {
                if (!FeatureLoader.Enabled<GameChatFeature>()) return true;
                if (string.IsNullOrEmpty(text))
                {
                    return true;
                }

                string trimmed = text.Trim();
                if (!trimmed.Equals("/help", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;                   // 只兜底 /help
                }

                return false;                      // 不广播
            }
        }
    }
}
