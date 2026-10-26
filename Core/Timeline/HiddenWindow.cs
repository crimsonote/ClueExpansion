#if TIMELINE   // 0.1.21 删除了回放时间线系统（TimelineRecorder / TimelineFrame 等）；定义 TIMELINE 才编译
using System;
using System.Collections.Generic;

namespace ClueDetail.Core.Timeline
{
    /// <summary>
    /// 复刻客户端的 <c>TimelineMirror.TryGetHiddenWindow</c>（0.1.20 :187157），
    /// 只保留服务端需要的那部分：算出未报告尸体会被划掉的隐藏起点。
    /// </summary>
    /// <remarks>
    /// 必须与客户端**逐字一致**：候选集、`AddOption` 的边界过滤、`Pick` 的哈希。
    /// 差一个候选，服务端算出的 <c>firstHidden</c> 就不在客户端的 <c>_hidden</c> 里，
    /// 占位帧会正常显示（泄露）。
    /// </remarks>
    internal static class HiddenWindow
    {
        private const int Width = 10;

        /// <param name="first">已发送帧里最早的常规帧时间（无则 int.MaxValue）。</param>
        /// <param name="last">已发送帧里最晚的常规帧时间（无则 int.MinValue）。</param>
        internal static int Predict(int deadTime, int victimId, int offset, int first, int last)
        {
            if (offset < 0 || first == int.MaxValue || last == int.MinValue)
            {
                return 0;
            }

            int num3 = deadTime - Mod(deadTime - offset, Width);
            int firstHidden2 = num3 == deadTime ? deadTime : num3 + Width;

            var list = new List<int>(3);
            if (num3 == deadTime)
            {
                AddOption(list, deadTime - Width, first, last);
                AddOption(list, deadTime, first, last);
            }
            else
            {
                AddOption(list, num3 - Width, first, last);
                AddOption(list, num3, first, last);
                AddOption(list, firstHidden2, first, last);
            }

            if (list.Count == 0)
            {
                return 0;
            }

            return list[Pick(deadTime, victimId, 0, list.Count)];
        }

        private static void AddOption(List<int> options, int time, int first, int last)
        {
            if (time >= first && time + Width <= last)
            {
                options.Add(time);
            }
        }

        private static int Mod(int a, int b)
        {
            int r = a % b;
            return r < 0 ? r + b : r;
        }

        private static int Pick(int deadTime, int victimId, int salt, int count)
        {
            int num = (deadTime * 73856093) ^ (victimId * 19349663) ^ (salt * 83492791);
            int num2 = (num ^ (num >>> 13)) * 1540483477;
            return (int)((uint)(num2 ^ (num2 >>> 15)) % (uint)count);
        }
    }
}
#endif