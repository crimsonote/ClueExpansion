using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.GameChat
{
    /// <summary>
    /// 房主侧：游戏内聊天框的**命令**（<c>/say</c>、<c>/clue</c>、<c>/help</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么是服务端功能</b>：命令要影响别人（私聊要只送达两个人）⇒ 必须在房主的进程里拦截
    /// <c>HostPacketHandler.Handle_C_CHAT_MESSAGE</c>（0.1.18 :177661，<c>public static</c>），
    /// 再用 <c>player.Session.Send(...)</c> 单发。客户端不需要装本模块。
    /// </para>
    /// <para>
    /// <b>与其它模块共存的三条约定</b>（2026-10-19 定案，见 <c>docs/游戏内命令.md</c>）：
    /// <list type="number">
    /// <item><b>只认领自己的命令名</b>：名字不匹配就 <c>return true</c> 放行 ⇒ 别人的命令原样流过去；</item>
    /// <item><b>认领了就吞</b>：处理完 <c>return false</c>，命令不会变成聊天广播；</item>
    /// <item><b><c>/help</c> 只"报到"不吞</b>：发自己的帮助后 <c>return true</c>，
    /// 让链上后面的模块（如捉迷藏）继续报各自的帮助 —— 这样**多个模块的帮助都能到达玩家**。</item>
    /// </list>
    /// ⚠️ 必须用 <c>[HarmonyPriority(Priority.First)]</c> 抢在默认优先级（捉迷藏是 <c>Normal</c>）之前，
    /// 否则对方先吞掉 <c>/help</c>，我们连报到机会都没有。
    /// </para>
    /// <para>
    /// <b>阶段门禁</b>：<c>/say</c> 只在<b>大厅与会议</b>可用；<c>/clue</c> 只在<b>会议</b>可用；
    /// <c>/help</c> 任何阶段都可报到。**发信机频道（<c>DeviceChat/SecretChat</c>）不参与**
    /// —— 非 <c>NormalChat</c> 一律放行给原版。
    /// </para>
    /// <para>
    /// <b>颜色</b>：服务端做不到 —— 客户端 <c>Handle_S_CHAT_MESSAGE</c> 会先
    /// <c>Util.NeutralizeRichText</c>（:43911）把 <c>&lt;</c> 换成 <c>&lt;+零宽字符</c>，
    /// 任何 <c>&lt;color&gt;</c> 都会失效。所以一律**纯文本标注**（如 <c>[私聊]</c>）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "GameChat",
        "房主侧：游戏内聊天框命令。 /say <昵称|ID> <消息> 私聊（仅大厅与会议，只有双方可见）； /clue [条件] 搜索自己搜集到的线索（仅会议）； /help 报到式帮助（不吞噬，其它模块的帮助也能显示）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class GameChatFeature
    {
        /// <summary>一行最多多少"显示宽度"（中文字按 2 计、ASCII 按 1 计）。</summary>
        /// <remarks>
        /// 聊天气泡宽度固定，超出的部分会被 TMP 直接截断（实机截图里就出现过
        /// "…最后被识别于身份验证屏幕" 被切掉）。所以先用它折行，再按 3 行一条打包。
        /// </remarks>
        public static ConfigEntry<int> MaxLineWidth { get; private set; }

        /// <summary>单条聊天消息最多渲染 3 行（原版聊天预制体的行数上限）。</summary>
        /// <remarks>
        /// 与捉迷藏的 <c>MaxLinesPerMessage = 3</c> 是同一条硬约束：超了会被截断或撑破布局，
        /// 所以长文本必须按 3 行一段拆成多条消息依次发。
        /// </remarks>
        internal const int MaxLinesPerMessage = 3;

        public static void Bind(ConfigFile config, string section)
        {
            MaxLineWidth = config.Bind(
                section,
                "MaxLineWidth",
                50,
                new ConfigDescription(
                    "一条线索最多显示多宽（中文算 2、ASCII 算 1）。超出的部分会折到下一行，\n" +
                    "折出来的行同样计入「每条消息最多 3 行」的上限。\n" +
                    "气泡放不下的行会被原版 TMP 直接截断，所以这个值不宜超过气泡实际宽度。",
                    new AcceptableValueRange<int>(20, 200)));
        }

        /// <summary>
        /// 注册本模块的聊天命令（启动时一次，与开关状态无关）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 命令的「声明」集中在这里：名字 / 别名 / 用法 / 说明 / 频道 / 阶段 / 是否吞包，
        /// 执行委托仍然指向各自的业务方法。新增命令只加一条
        /// <see cref="ChatCommandDef"/>，<see cref="ChatCommandHook"/> 与 <c>/help</c> 都自动跟上。
        /// </para>
        /// <para>
        /// ⚠️ 阶段门禁（<c>/say</c> 大厅/会议/调查、<c>/clue</c> 仅会议、<c>/f</c> 仅生存）
        /// 仍在各命令内部，表里的 <see cref="ChatCommandDef.HelpPhases"/> 只用于帮助列表过滤 ——
        /// 这样迁移前后行为逐字不变。等抽成共享库时再把门禁上移。
        /// </para>
        /// </remarks>
        public static void OnLoaded()
        {
            ChatCommandTable.Clear();

            ChatCommandTable.Register(new ChatCommandDef
            {
                Name = "say",
                Aliases = new[] { "s" },
                Usage = "<昵称|ID> <消息>",
                Description = "私聊（= /say）",
                Channels = ChatChannelMask.Public,
                HelpPhases = ChatPhaseMask.Lobby | ChatPhaseMask.Trial | ChatPhaseMask.Detective,
                Swallow = true,
                Handler = ctx => CmdSay(ctx.Room, ctx.Player, ctx.Args),
            });

            ChatCommandTable.Register(new ChatCommandDef
            {
                Name = "clue",
                Aliases = new[] { "c" },
                Usage = "[条件...]",
                Description = "搜索线索（= /clue）",
                ExtraHelp = new[] { "/c help  完整语法（t: p: r: a: id: 等）" },
                Channels = ChatChannelMask.Public,
                HelpPhases = ChatPhaseMask.Trial,
                Swallow = true,
                Handler = ctx => CmdClue(ctx.Room, ctx.Player, ctx.Args),
            });

            ChatCommandTable.Register(new ChatCommandDef
            {
                Name = "who",
                Aliases = new[] { "w" },
                Usage = string.Empty,
                Description = "玩家 ID 与昵称（= /who）",
                Channels = ChatChannelMask.Public,
                HelpPhases = ChatPhaseMask.Any,
                Swallow = true,
                Handler = ctx => CmdWho(ctx.Room, ctx.Player),
            });

            ChatCommandTable.Register(new ChatCommandDef
            {
                Name = "help",
                Aliases = new[] { "h" },
                Usage = string.Empty,
                Description = "这份帮助（= /help）",
                Channels = ChatChannelMask.Public,
                HelpPhases = ChatPhaseMask.Any,
                // ⚠️ 只报到、不吞：让链上后面的模块（捉迷藏等）继续报各自的帮助。
                Swallow = false,
                Handler = ctx => CmdHelp(ctx.Room, ctx.Player),
            });

            ChatCommandTable.Register(new ChatCommandDef
            {
                Name = "fake",
                Aliases = new[] { "f" },
                Usage = "[时间] [人物] [房间] [活动]",
                Description = "线索伪造（黑方秘密终端）",
                // Public / Device 也认领：在这两处输入 /f 要给一句「只能在黑方秘密终端使用」的提示，
                // 不能放行成普通聊天/发信机留言（会议阶段会广播出去）。
                Channels = ChatChannelMask.Public | ChatChannelMask.Device | ChatChannelMask.Secret,
                // 只有黑方/黑幕认领；白方输入 /f 不拦截（像本模块没注册过）。
                Side = ChatSide.Black,
                HelpPhases = ChatPhaseMask.Survive,
                Swallow = true,
                Handler = ctx => ClueDetail.Features.Host.ClueFake.ClueFakeCommand.Handle(
                    ctx.Room, ctx.Player, ctx.DeviceId, ctx.Args, ctx.Channel),
            });
        }
    }
}