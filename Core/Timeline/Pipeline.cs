﻿﻿using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Protocol;
using ServerTimelineRecorder = Server.Game.TimelineRecorder;

namespace ClueDetail.Core.Timeline
{
    /// <summary>
    /// 回放帧管线的**唯一挂点**。各功能只注册阶段处理器，不再各自 Harmony 挂
    /// <c>TimelineRecorder.Send</c> / <c>OnSurvivalTick</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2026-10-26 引入。动机：<c>ShootReminder</c>（录制节拍）与
    /// <c>ShadowSabotage</c>（时钟污染缓存/打乱）各自挂 <c>Send</c>，
    /// 将来再加「未报告占位」就会三方抢同一个 Prefix，顺序只能靠加载顺序
    /// 隐式决定，无法回归。
    /// </para>
    /// <para>
    /// **当前是空壳**：没有注册任何处理器，所有 Prefix 一律放行 ⇒ 行为与原版完全一致。
    /// 后续按步骤迁入：① <c>ShootReminder</c> 的 cron/占位；② <c>ShadowSabotage</c> 的时钟逻辑。
    /// </para>
    /// <para>
    /// ⚠️ 处理器按 <see cref="Entry.Priority"/> **降序**执行；任一处理器返回 <c>false</c>
    /// 即拦截本帧（不再交给原版）。与 Harmony 的 <c>Priority</c> 语义一致（值大者先跑）。
    /// </para>
    /// </remarks>
    internal static class TimelinePipeline
    {
        /// <summary>阶段。顺序固定：Plan → Capture → Transform。</summary>
        internal enum Stage
        {
            /// <summary>每个生存 tick：决定要不要采集这一帧。</summary>
            Plan,

            /// <summary>采集之后：可替换/补充帧（占位、纠正时间）。</summary>
            Capture,

            /// <summary>发送之前：可改时间/内容，或拦截。</summary>
            Transform,
        }

        /// <summary>处理器签名：返回 false = 拦截。</summary>
        internal delegate bool Handler(TimelineFrame frame, int surviveTime, Stage stage);

        private sealed class Entry
        {
            internal string Name;
            internal Stage Stage;
            internal int Priority;
            internal Handler Handler;
        }

        private static readonly List<Entry> Handlers = new List<Entry>();
        private static bool _mounted;

        /// <summary>注册一个阶段处理器（重复注册同名会覆盖）。</summary>
        internal static void Register(string name, Stage stage, int priority, Handler handler)
        {
            if (string.IsNullOrEmpty(name) || handler == null)
            {
                return;
            }

            Handlers.RemoveAll(e => e.Name == name);
            Handlers.Add(new Entry
            {
                Name = name,
                Stage = stage,
                Priority = priority,
                Handler = handler,
            });
            Handlers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>挂上唯一的两个 Prefix。只允许调用一次。</summary>
        internal static void Mount(Harmony harmony)
        {
            if (_mounted || harmony == null)
            {
                return;
            }

            MethodInfo send = AccessTools.Method(
                typeof(ServerTimelineRecorder), "Send", new[] { typeof(TimelineFrame) });
            if (send != null)
            {
                harmony.Patch(send,
                    prefix: new HarmonyMethod(typeof(TimelinePipeline), nameof(SendPrefix)));
            }
            else
            {
                global::ClueDetail.Core.Log.Warn("回放管线：找不到 TimelineRecorder.Send，Send 阶段不可用。");
            }

            MethodInfo tick = AccessTools.Method(
                typeof(ServerTimelineRecorder), nameof(ServerTimelineRecorder.OnSurvivalTick), new[] { typeof(int) });
            if (tick != null)
            {
                harmony.Patch(tick,
                    prefix: new HarmonyMethod(typeof(TimelinePipeline), nameof(TickPrefix)));
            }
            else
            {
                global::ClueDetail.Core.Log.Warn("回放管线：找不到 TimelineRecorder.OnSurvivalTick，Plan 阶段不可用。");
            }

            MethodInfo clear = AccessTools.Method(
                typeof(ServerTimelineRecorder), nameof(ServerTimelineRecorder.Clear));
            if (clear != null)
            {
                harmony.Patch(clear,
                    postfix: new HarmonyMethod(typeof(TimelinePipeline), nameof(ClearPostfix)));
            }

            _mounted = true;
            global::ClueDetail.Core.Log.Info($"回放管线已挂载（处理器 {Handlers.Count} 个）。");
        }

        /// <summary>已经实际发出去的帧时间（供占位帧复刻客户端的常规帧范围）。</summary>
        private static readonly HashSet<int> Sent = new HashSet<int>();

        internal static int FirstRegular(int offset)
        {
            int first = int.MaxValue;
            foreach (int t in Sent)
            {
                if (offset >= 0 && ((t - offset) % 10 + 10) % 10 == 0 && t < first)
                {
                    first = t;
                }
            }

            return first;
        }

        internal static int LastRegular(int offset)
        {
            int last = int.MinValue;
            foreach (int t in Sent)
            {
                if (offset >= 0 && ((t - offset) % 10 + 10) % 10 == 0 && t > last)
                {
                    last = t;
                }
            }

            return last;
        }

        internal static void ClearSent()
        {
            Sent.Clear();
        }

        private static void ClearPostfix()
        {
            Sent.Clear();       // 换局：新的一局从头记录帧时间
        }

        private static bool SendPrefix(TimelineFrame frame)
        {
            bool pass = Run(frame, 0, Stage.Transform);
            if (pass && frame != null)
            {
                Sent.Add(frame.Time);
            }

            return pass;
        }

        private static bool TickPrefix(int surviveTime)
        {
            return Run(null, surviveTime, Stage.Plan);
        }

        private static bool Run(TimelineFrame frame, int surviveTime, Stage stage)
        {
            if (Handlers.Count == 0)
            {
                return true;    // 空壳：原样放行
            }

            for (int i = 0; i < Handlers.Count; i++)
            {
                Entry entry = Handlers[i];
                if (entry.Stage != stage)
                {
                    continue;
                }

                try
                {
                    if (!entry.Handler(frame, surviveTime, stage))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(
                        ex, $"回放管线：处理器「{entry.Name}」在 {stage} 阶段抛异常（放行）。");
                }
            }

            return true;
        }
    }
}
