using DummyClient;
using HarmonyLib;
using ClueDetail.Core;
using Protocol;
using ServerGameHostPacketHandler = Server.Game.HostPacketHandler;
using ServerGameHostPeerSession = Server.Game.HostPeerSession;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.GameChat
{
    internal partial class GameChatFeature
    {
        /// <summary>
        /// 拦截玩家的聊天包，识别本模块命令。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ <c>[HarmonyPriority(Priority.First)]</c> 是**必需**的：捉迷藏的游戏内命令也挂在这里
        /// （默认 <c>Normal</c> 优先级）且会吞掉 <c>/help</c>；我们必须先报到，它才能继续报它的帮助。
        /// </para>
        /// <para>
        /// ⚠️ 只处理 <c>NormalChat</c>：<c>DeviceChat</c> / <c>SecretChat</c> 是发信机频道，
        /// 走原版逻辑（含占用判定、密聊黑方闸门、冷却），本模块**不参与**。
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

                    if (msg.Type != EChatType.NormalChat)
                    {
                        return true;              // 发信机 / 密聊频道不参与
                    }

                    string text = (msg.Text ?? string.Empty).Trim();
                    if (text.Length < 2 || text[0] != '/')
                    {
                        return true;              // 不是命令
                    }

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

                    string[] parts = text.Substring(1)
                        .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        return true;
                    }

                    string name = parts[0].ToLowerInvariant();
                    if (!IsOwnCommand(name))
                    {
                        return true;              // ① 只认领自己的名字 ⇒ 放行给别人
                    }

                    string arg = parts.Length > 1
                        ? string.Join(" ", parts, 1, parts.Length - 1)
                        : string.Empty;

                    room.Push(delegate { Dispatch(room, player, name, arg); });

                    // ③ /help 只报到不吞：让链上后面的模块继续报各自的帮助。
                    if (name == "help")
                    {
                        return true;
                    }

                    return false;                 // ② 认领了就吞
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "游戏内命令：拦截失败（交还原版）。");
                    return true;
                }
            }

        /// <summary>这个命令名（含单字母别名）是否属于本模块。</summary>
        private static bool IsOwnCommand(string name)
        {
            name = Canonical(name);   // 先规范化：/c 之类也要算自家命令

            for (int i = 0; i < OwnCommands.Length; i++)
            {
                if (string.Equals(OwnCommands[i], name, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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