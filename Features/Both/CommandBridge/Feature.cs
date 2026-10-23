using ClueDetail.Core;

namespace ClueDetail.Features.Both.CommandBridge
{
    /// <summary>
    /// 实验性：把本模块的命令接进 DT_Tools 的**命令桥**（终端 / WebUI / MCP 三个入口共用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与捉迷藏 <c>Console/ConsoleBridge.cs</c> 同一套接线（2026-10-19 照它复刻），两步：
    /// </para>
    /// <list type="number">
    /// <item><b>执行</b>：Prefix 挂 <c>DT_Tools.WebConsole.WebConsole.ExecuteCommandText(string)</c>
    /// （2.0.0.6 实测仍在，<c>internal static</c>）—— 命中本模块命令就自己出结果并拦掉原版；</item>
    /// <item><b>列表</b>：反射重建 <c>CommandsApi._cachedJson</c>（它只在上游**首次请求**时生成，
    /// 而我们的补丁挂得比它晚）—— 不重建的话命令**能执行但补全里看不到**。</item>
    /// </list>
    /// <para>
    /// ⚠️ 全链路可选：DT_Tools 没装、或本功能被关掉，必须**整类**跳过 ——
    /// 只跳过一半的话，命令会出现在列表里却执行不了（比不出现更糟）。
    /// ⚠️ 房主门禁不靠上游：我们的 Prefix 跑在上游派发**之前**，所以由命令自己判
    /// （<c>ServerGameRoom.Instance</c> 只在服务端存在）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "CommandBridge",
        "实验性：把本模块的命令接进 DT_Tools 的命令桥（终端 / WebUI / MCP 都能执行、能补全）。命令：cd_sabotage（强制开关破坏点）、cd_impersonate（调试：把某个玩家的操作记为另一个玩家）。",
        defaultEnabled: true,
        side: FeatureSide.Both)]
    internal partial class CommandBridgeFeature
    {
        /// <summary>命令表：补进 DT_Tools 的 <c>/api/commands</c>（形状与上游一致）。</summary>
        internal static readonly (string Name, string[] Aliases, string Usage, string Description)[] Commands =
        {
            (
                "cd_sabotage",
                new[] { "cdsab" },
                "cd_sabotage <clock|identity|both> <on|off|status>",
                "实验性：强制开启 / 关闭隐蔽破坏点。clock = 时钟记录组件（时间污染），" +
                "identity = 身份识别组件（匿名污染），both = 两个一起；status 只看当前状态。仅房主可用。"),
            (
                "cd_impersonate",
                new[] { "cdimp" },
                "cd_impersonate <源玩家ID> <目标玩家ID> [秒] | off | status",
                "调试：一段时间内把「源玩家」完成的设备使用记为「目标玩家」，用来验证" +
                "「别人完成的线索在身份破坏期间是否显示为某人」。只影响源玩家，其他人不受影响。"),
        };

        /// <summary>补丁挂好之后重建上游命令表缓存。</summary>
        public static void OnPatched()
        {
            RefreshCommandList();
        }
    }
}