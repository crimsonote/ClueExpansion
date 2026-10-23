using System;
using BepInEx.Configuration;

namespace ClueDetail.Core
{
    /// <summary>日志门面：全项目只经这里输出，便于统一前缀与后续收敛。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 2026-10-21（用户口径）：**Info 级可整体关闭**。BepInEx 的 <c>LogInfo</c> 会同步写盘，
    /// 而本模块每秒（<c>SurvivalTick</c>）都有维护日志的潜在调用点，长时间对局会持续追加
    /// <c>LogOutput.log</c>。关掉它只影响"进度类"输出，<see cref="Warn"/> / <see cref="Error"/> /
    /// <see cref="Exception"/> 仍然照常写 —— 出错信息永远不该被静音。
    /// </para>
    /// <para>
    /// 开关是热读的（每次判断都读 <c>ConfigEntry.Value</c>），改 <c>.cfg</c> 立即生效。
    /// </para>
    /// </remarks>
    internal static class Log
    {
        private const string Tag = "[ClueDetail] ";

        /// <summary><c>[Log] Enabled</c>：Info 级日志总开关（默认开）。</summary>
        private static ConfigEntry<bool> _infoEnabled;

        /// <summary>绑定 <c>[Log]</c> 段。由 <c>Plugin.Start</c> 在装载功能之前调用一次。</summary>
        internal static void Bind(ConfigFile cfg)
        {
            if (cfg == null)
            {
                return;
            }

            _infoEnabled = cfg.Bind(
                "Log",
                "Enabled",
                true,
                "是否输出常规信息日志（Info 级）。\n" +
                "关掉它可以显著减少 BepInEx\\LogOutput.log 的写盘量 —— 本模块每秒都有维护点，\n" +
                "长局会持续追加。警告与错误不受影响，永远照常输出。\n" +
                "改完立即生效，不必重启游戏。");
        }

        /// <summary>Info 级是否放行（未绑定时默认放行，避免 Bind 之前的两行启动日志丢失）。</summary>
        private static bool InfoEnabled => _infoEnabled == null || _infoEnabled.Value;

        public static void Info(string msg)
        {
            if (!InfoEnabled)
            {
                return;
            }

            Plugin.Log?.LogInfo(Tag + msg);
        }

        public static void Warn(string msg)
        {
            Plugin.Log?.LogWarning(Tag + msg);
        }

        public static void Error(string msg)
        {
            Plugin.Log?.LogError(Tag + msg);
        }

        public static void Exception(Exception ex, string context = null)
        {
            Plugin.Log?.LogError(Tag + (context == null ? string.Empty : context + " ") + ex);
        }
    }
}
