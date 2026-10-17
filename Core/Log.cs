using System;

namespace ClueDetail.Core
{
    /// <summary>日志门面：全项目只经这里输出，便于统一前缀与后续收敛。</summary>
    internal static class Log
    {
        private const string Tag = "[ClueDetail] ";

        public static void Info(string msg)
        {
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
