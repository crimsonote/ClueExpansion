using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ClueDetail.Core.Net
{
    /// <summary>
    /// 自定义协议消息基类：一张稀疏的 varint 字段表。
    /// </summary>
    /// <remarks>
    /// **形状是冲着可扩展性选的**：加一个字段 = 用一个新的字段号 + 加一个属性，
    /// 序列化/反序列化代码一行都不用动（`FieldCount` 之类的通用实现已经覆盖）。
    /// <para>
    /// 只支持 varint（32 位有符号整数）字段 —— 本模块要传的都是 id / 枚举 / 开关，
    /// 够用，而且编解码最省、不需要 `WireType`（那是 internal 类型）。
    /// </para>
    /// <para>
    /// 与游戏原版的兼容性靠三件事保证（见 <see cref="CdWire"/>）：
    /// ① opcode 取自原版绝不会用到的高位段；
    /// ② 注册前检查该 opcode 是否已被占用，撞车就放弃注册而不是覆盖；
    /// ③ 只有本模块自己的消息才会被拦截改道，原版消息一律走原路径。
    /// </para>
    /// </remarks>
    internal abstract class CdMessage : IMessage
    {
        private readonly Dictionary<int, int> _fields = new Dictionary<int, int>();

        /// <summary>本消息的 opcode（<see cref="CdOpcode"/> 里分配）。</summary>
        public abstract ushort Opcode { get; }

        /// <summary>已设置的字段个数（自检/日志用）。</summary>
        public int FieldCount => _fields.Count;

        protected void Set(int field, int value)
        {
            _fields[field] = value;
        }

        protected int Get(int field)
        {
            return _fields.TryGetValue(field, out int value) ? value : 0;
        }

        /// <summary>恒为 null：本模块的消息不走标准 <c>Send(IMessage)</c> 路径，没有人读它。</summary>
        public MessageDescriptor Descriptor => null;

        public void WriteTo(CodedOutputStream output)
        {
            foreach (var pair in _fields)
            {
                if (pair.Value == 0)
                {
                    continue;
                }

                // wire type 0 = varint；手写 tag 是因为 WireType 枚举不可见。
                output.WriteTag((uint)(pair.Key << 3));
                output.WriteInt32(pair.Value);
            }
        }

        public int CalculateSize()
        {
            int size = 0;
            foreach (var pair in _fields)
            {
                if (pair.Value == 0)
                {
                    continue;
                }

                size += CodedOutputStream.ComputeTagSize(pair.Key) + CodedOutputStream.ComputeInt32Size(pair.Value);
            }

            return size;
        }

        public void MergeFrom(CodedInputStream input)
        {
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if ((tag & 7) != 0)
                {
                    // 本协议只用 varint；遇到别的 wire type 说明不是我们的包，停止解析。
                    return;
                }

                _fields[(int)(tag >> 3)] = input.ReadInt32();
            }
        }
    }
}
