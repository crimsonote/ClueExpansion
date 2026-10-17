namespace ClueDetail.Core.Net
{
    /// <summary>客户端 → 服务端：我装了本模块。字段 1 = 能力版本。</summary>
    internal sealed class CdHello : CdMessage
    {
        public override ushort Opcode => CdOpcode.Hello;

        public int Version
        {
            get => Get(1);
            set => Set(1, value);
        }
    }

    /// <summary>
    /// 服务端 → 客户端：房主侧开关状态。
    /// </summary>
    /// <remarks>
    /// 字段 1 = 是否启用，字段 2 = 房主侧版本。
    /// 客户端据此区分"房主没装本模块"（收不到 → 允许自采集兜底）与
    /// "房主装了但关闭"（Enabled = 0 → 跟随关闭，连自采集一起停）。
    /// </remarks>
    internal sealed class CdState : CdMessage
    {
        public override ushort Opcode => CdOpcode.State;

        public int Enabled
        {
            get => Get(1);
            set => Set(1, value);
        }

        public int HostVersion
        {
            get => Get(2);
            set => Set(2, value);
        }
    }
}