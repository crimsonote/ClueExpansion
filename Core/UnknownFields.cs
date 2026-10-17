using System;
using System.Collections;
using System.Reflection;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 在 <see cref="PropositionInfo"/> 的 protobuf 未知字段区读写本模块的旁路数据。
    /// </summary>
    /// <remarks>
    /// 背景：0.1.18 删除了 <c>PropositionInfo.WhatType</c>（原字段号 4）且**未复用**该号。
    /// protobuf 的 wire 规则要求解析方原样保留未定义字段，所以：
    /// <list type="bullet">
    /// <item>服务端把数据写进未知字段 → 序列化后随包下发；</item>
    /// <item>0.1.18 原版客户端解析时把它丢进自己的 _unknownFields 并忽略 —— **不会报错、不改变任何原版行为**；</item>
    /// <item>装了本模块的客户端用这里的方法读回。</item>
    /// </list>
    /// 这条通道不需要（也无法）新增 opcode：<c>ServerSession.Send</c> 用
    /// <c>Enum.Parse(PacketID, packet.Descriptor.Name)</c> 反解 opcode，自定义消息发不出去。
    /// <para>字段分配（均取自 0.1.18 未使用且未复用的号）：</para>
    /// <list type="bullet">
    /// <item>4 — 详细活动 EWhatType（<see cref="SetWhat"/> / <see cref="GetWhat"/>）</item>
    /// <item>6 — 房主侧状态：1 = 启用详情下发，0 = 房主装了但关闭（<see cref="SetFlag"/> 等）</item>
    /// </list>
    /// <para>
    /// 实现约束：<c>Google.Protobuf.UnknownFieldSet</c> 是 public，但它返回的
    /// <c>UnknownField</c> 是 internal，因此只能用反射链（GetOrAddField → AddVarint）。
    /// 已用 0.1.18 的反射核对过成员名：_unknownFields / fields / GetOrAddField / AddVarint / varintList。
    /// </para>
    /// </remarks>
    internal static class UnknownFields
    {
        /// <summary>详细活动（EWhatType）。</summary>
        public const int WhatFieldNumber = 4;

        /// <summary>房主侧状态开关。</summary>
        public const int FlagFieldNumber = 6;

        private static readonly FieldInfo SetField;
        private static readonly Type SetType;
        private static readonly FieldInfo FieldsField;
        private static readonly MethodInfo GetOrAddField;

        static UnknownFields()
        {
            try
            {
                SetField = typeof(PropositionInfo).GetField("_unknownFields", BindingFlags.Instance | BindingFlags.NonPublic);
                if (SetField == null)
                {
                    return;
                }

                SetType = SetField.FieldType;
                FieldsField = SetType.GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic);
                GetOrAddField = SetType.GetMethod("GetOrAddField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "反射定位 protobuf 未知字段失败");
            }
        }

        public static bool Available => SetField != null && SetType != null && FieldsField != null && GetOrAddField != null;

        public static bool SetWhat(PropositionInfo info, int what)
        {
            return what != 0 && SetValue(info, WhatFieldNumber, what);
        }

        public static int GetWhat(PropositionInfo info)
        {
            return TryGetValue(info, WhatFieldNumber, out int value) ? value : 0;
        }

        /// <summary>
        /// 写房主侧状态。**0 也要写** —— 它表示"房主装了但关闭"，必须与"没装"（无标记）区分开。
        /// </summary>
        public static bool SetFlag(PropositionInfo info, bool enabled)
        {
            if (HasValue(info, FlagFieldNumber))
            {
                return true;
            }

            return SetValue(info, FlagFieldNumber, enabled ? 1 : 0);
        }

        /// <summary>读房主侧状态；没有标记时返回 true（调用方应先用 <see cref="HasFlag"/> 区分）。</summary>
        public static bool GetFlag(PropositionInfo info)
        {
            return !TryGetValue(info, FlagFieldNumber, out int value) || value != 0;
        }

        public static bool HasFlag(PropositionInfo info)
        {
            return HasValue(info, FlagFieldNumber);
        }

        // ── 底层 ────────────────────────────────────────────────────────────

        public static bool SetValue(PropositionInfo info, int fieldNumber, int value)
        {
            if (info == null || !Available)
            {
                return false;
            }

            try
            {
                object set = SetField.GetValue(info) ?? Activator.CreateInstance(SetType, true);
                object field = GetOrAddField.Invoke(set, new object[] { fieldNumber });
                if (field == null)
                {
                    return false;
                }

                MethodInfo addVarint = field.GetType().GetMethod("AddVarint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (addVarint == null)
                {
                    return false;
                }

                addVarint.Invoke(field, new object[] { (ulong)value });
                SetField.SetValue(info, set);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "写入线索未知字段失败（字段号 " + fieldNumber + "）");
                return false;
            }
        }

        public static bool TryGetValue(PropositionInfo info, int fieldNumber, out int value)
        {
            value = 0;
            if (!TryGetField(info, fieldNumber, out object field))
            {
                return false;
            }

            try
            {
                FieldInfo listField = field.GetType().GetField("varintList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(listField?.GetValue(field) is IList values) || values.Count == 0)
                {
                    return false;
                }

                value = (int)Convert.ToUInt64(values[0]);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "读取线索未知字段失败（字段号 " + fieldNumber + "）");
                return false;
            }
        }

        public static bool HasValue(PropositionInfo info, int fieldNumber)
        {
            return TryGetField(info, fieldNumber, out _);
        }

        private static bool TryGetField(PropositionInfo info, int fieldNumber, out object field)
        {
            field = null;
            if (info == null || !Available)
            {
                return false;
            }

            try
            {
                object set = SetField.GetValue(info);
                if (set == null || !(FieldsField.GetValue(set) is IDictionary fields) || !fields.Contains(fieldNumber))
                {
                    return false;
                }

                field = fields[fieldNumber];
                return field != null;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "读取线索未知字段容器失败（字段号 " + fieldNumber + "）");
                return false;
            }
        }
    }
}
