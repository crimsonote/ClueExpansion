#if TIMELINE   // 回放提醒的一部分；0.1.21 删除回放系统后不参与编译
using System;

namespace ClueDetail.Features.Host.ShootReminder
{
    /// <summary>简化 cron：<c>default</c> / <c>never</c> / <c>*/N *</c> / <c>M */H</c>。</summary>
    /// <remarks>
    /// 单位是**游戏时间**：1 分钟 = 1 秒（<c>SurviveTime</c>）。
    /// 所有判断都基于 <c>rel = surviveTime - offset</c>，offset 由本模块每局随机。
    /// </remarks>
    internal static class CronSchedule
    {
        internal static bool IsDefault(string cron)
        {
            return string.IsNullOrWhiteSpace(cron)
                || cron.Trim().Equals("default", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsNever(string cron)
        {
            string t = (cron ?? string.Empty).Trim().ToLowerInvariant();
            return t == "never" || t == "-" || t == "0";
        }

        /// <summary>判断相对偏移后的秒数是否命中节拍。</summary>
        internal static bool Matches(string cron, int rel)
        {
            if (rel < 0)
            {
                return false;
            }

            if (IsNever(cron))
            {
                return false;
            }

            string t = (cron ?? string.Empty).Trim().ToLowerInvariant();

            // */N * —— 每 N 分钟
            if (t.StartsWith("*/"))
            {
                if (int.TryParse(t.Substring(2), out int n))
                {
                    if (n < 5)
                    {
                        n = 5;      // 不比 5 分钟更密
                    }

                    return rel % n == 0;
                }

                return true;        // 解析失败 ⇒ 放行
            }

            // M */H —— 每 H 小时的 M 分钟
            string[] parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].StartsWith("*/"))
            {
                if (int.TryParse(parts[0], out int minute)
                    && int.TryParse(parts[1].Substring(2), out int hours)
                    && hours > 0)
                {
                    minute = Math.Max(0, Math.Min(59, minute));
                    return rel % (hours * 60) == minute;
                }
            }

            return true;            // 解析失败 ⇒ 放行
        }
    }
}
#endif