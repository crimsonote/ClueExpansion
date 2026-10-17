namespace ClueDetail.Core.Net
{
    /// <summary>
    /// 本模块专属的 opcode 段 —— 只承载**没有宿主对象可附着**的独立事件。
    /// </summary>
    /// <remarks>
    /// 线索的"详细活动"不走这里：它是线索的一个属性，用 protobuf 未知字段附着在
    /// <c>PropositionInfo</c> 上更自然（见 <see cref="UnknownFields"/>），
    /// 不需要关联键、不需要保证时序。这里只放握手与状态这类独立消息。
    /// <para>
    /// 原版现用 1000–1200。这里取 ushort 高位区：原版按顺序递增到不了 61000。
    /// 而且 <see cref="CdWire.RegisterAll"/> 注册前会检查 <c>PacketManager._makeFunc</c>
    /// 是否已占用该号 —— 撞车就**放弃注册并报错**，绝不覆盖（覆盖等于弄坏原版那条包）。
    /// </para>
    /// <para>新增消息：在 <see cref="Count"/> 之前追加常量并同步 +1，**别改已有的号**。</para>
    /// </remarks>
    internal static class CdOpcode
    {
        /// <summary>本模块 opcode 段起点。</summary>
        public const ushort Base = 61000;

        /// <summary>客户端 → 服务端：我装了本模块（附带能力版本）。</summary>
        public const ushort Hello = Base + 0;

        /// <summary>服务端 → 客户端：房主侧开关状态。</summary>
        public const ushort State = Base + 1;

        /// <summary>本段已分配的个数（自检用）。</summary>
        public const ushort Count = 2;
    }
}