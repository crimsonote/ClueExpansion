namespace ClueDetail.Core
{
    /// <summary>
    /// 原版「线索 30 秒合并窗口」的**可配置**版本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版 <c>Device.RecordLastUsingPlayer</c>（0.1.18 :166404）写死了
    /// <c>limitTime = surviveTime / 30 * 30</c>：**同一台设备 + 同一身份 + 同一个 30 秒桶**内
    /// 反复使用只留一条（那条的时间被刷成最后一次）。
    /// </para>
    /// <para>
    /// 本类把这把尺子交给配置：
    /// <list type="bullet">
    /// <item><c>30</c>（默认）= 原版口径 —— 调用方**直接放行原方法**，一行都不碰；</item>
    /// <item><c>0</c> = **禁用合并**：每次使用都新起一条；</item>
    /// <item>其它值 = 自定义桶长（例如 60 会覆盖更多线索）。</item>
    /// </list>
    /// ⚠️ 窗口是**滚动桶**（<c>surviveTime / 秒数 * 秒数</c>），不是滑动窗口；桶边界对齐"生存阶段计时"，
    /// 与系统时间、游戏内"时刻"无关。0.1.17a 与 0.1.18 的原版写法逐字相同
    /// （<c>0.1.17a :165655</c> / <c>0.1.18 :166404</c>）。
    /// </para>
    /// </remarks>
    internal static class ClueMergeWindow
    {
        /// <summary>
        /// 取当前桶长的入口，由 <c>Features/Host/ClueMerge</c> 绑定到配置项。
        /// </summary>
        /// <remarks>
        /// 刻意存成委托而不是直接存 <c>ConfigEntry</c>：Core 层不认识配置体系，
        /// 而且这样每次取都是**热读**（改 .cfg 立刻生效，与其它开关一致）。
        /// </remarks>
        public static global::System.Func<int> Provider = () => 30;

        /// <summary>当前配置的桶长（秒）。</summary>
        public static int Seconds
        {
            get
            {
                global::System.Func<int> provider = Provider;
                return provider != null ? provider() : 30;
            }
        }

        /// <summary>是否就是原版口径（30 秒）—— 是的话调用方应当完全放行原版方法。</summary>
        public static bool IsVanilla => Seconds == 30;

        /// <summary>
        /// 当前桶的起点。
        /// </summary>
        /// <remarks>
        /// **禁用合并时返回 <see cref="int.MaxValue"/>**：原版谓词是 <c>x.Time &gt;= limitTime</c>，
        /// 于是任何既有线索都匹配不上 ⇒ 每次使用都追加一条（这正是"0 = 禁用合并"想要的效果）。
        /// </remarks>
        public static int LimitTime(int surviveTime)
        {
            int seconds = Seconds;
            if (seconds <= 0)
            {
                return int.MaxValue;
            }

            return surviveTime / seconds * seconds;
        }
    }
}
