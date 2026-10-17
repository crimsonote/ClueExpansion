using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Google.Protobuf;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 在任意 protobuf 消息的未知字段区读写本模块的旁路数据。
    /// </summary>
    /// <remarks>
    /// 背景：0.1.18 删除了 <c>PropositionInfo.WhatType</c>（原字段号 4）且**未复用**该号。
    /// protobuf 的 wire 规则要求解析方原样保留未定义字段，所以：
    /// <list type="bullet">
    /// <item>发送方把数据写进未知字段 → 序列化后随包下发；</item>
    /// <item>0.1.18 原版客户端/服务端解析时把它丢进自己的 _unknownFields 并忽略 ——
    /// **不会报错、不改变任何原版行为**；</item>
    /// <item>装了本模块的一端用这里的方法读回。</item>
    /// </list>
    /// 这条通道不需要（也无法）新增 opcode：<c>ServerSession.Send</c> 用
    /// <c>Enum.Parse(PacketID, packet.Descriptor.Name)</c> 反解 opcode，自定义消息发不出去。
    /// <para>字段分配（都取自 0.1.18 未使用且未复用的号）：</para>
    /// <list type="bullet">
    /// <item><c>PropositionInfo</c> 4 — 详细活动 EWhatType</item>
    /// <item><c>PropositionInfo</c> 6 — 房主侧状态：1 = 启用详情下发，0 = 房主装了但关闭</item>
    /// <item><c>C_SCAN_DEVICE</c> 2 — 客户端能力上报（值 = 本模块的能力版本）</item>
    /// </list>
    /// <para>
    /// 实现约束：<c>Google.Protobuf.UnknownFieldSet</c> 是 public，但它返回的
    /// <c>UnknownField</c> 是 internal，因此只能用反射链（GetOrAddField → AddVarint）。
    /// 已用 0.1.18 的反射核对过成员名：_unknownFields / fields / GetOrAddField / AddVarint / varintList。
    /// </para>
    /// </remarks>
    internal static class UnknownFields
    {
        /// <summary>详细活动（EWhatType），载体是 PropositionInfo。</summary>
        public const int WhatFieldNumber = 4;

        /// <summary>房主侧状态开关，载体是 PropositionInfo。</summary>
        public const int FlagFieldNumber = 6;

        private static readonly Dictionary<Type, FieldInfo> SetFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> FieldsFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, MethodInfo> GetOrAddFieldCache = new Dictionary<Type, MethodInfo>();

        // ── 便捷重载（线索相关，载体固定是 PropositionInfo）─────────────────

        public static bool SetWhat(PropositionInfo info, int what)
        {
            return what != 0 && SetValue(info, WhatFieldNumber, what);
        }

        public static int GetWhat(PropositionInfo info)
        {
            return TryGetValue(info, WhatFieldNumber, out int value) ? value : 0;
        }

        /// <summary>
        /// 覆盖写详细活动（先清掉旧值）。
        /// </summary>
        /// <remarks>
        /// <see cref="SetWhat"/> 是幂等的（防重复追加），但伪证界面里玩家会反复改选，
        /// 那种场景需要真正覆盖，所以额外提供这一条。
        /// </remarks>
        public static bool ReplaceWhat(PropositionInfo info, int what)
        {
            if (info == null || what == 0)
            {
                return false;
            }

            ClearValue(info, WhatFieldNumber);
            return SetValue(info, WhatFieldNumber, what);
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

        // ── 泛型底层（任意 IMessage）────────────────────────────────────────

        public static bool SetValue(IMessage message, int fieldNumber, int value)
        {
            if (message == null)
            {
                return false;
            }

            try
            {
                Type type = message.GetType();
                FieldInfo setField = SetFieldOf(type);
                FieldInfo fieldsField = FieldsFieldOf(type);
                MethodInfo getOrAdd = GetOrAddFieldOf(type);
                if (setField == null || fieldsField == null || getOrAdd == null)
                {
                    return false;
                }

                object set = setField.GetValue(message) ?? Activator.CreateInstance(setField.FieldType, true);
                object field = getOrAdd.Invoke(set, new object[] { fieldNumber });
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
                setField.SetValue(message, set);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "写入未知字段失败（字段号 " + fieldNumber + "）");
                return false;
            }
        }

        public static bool TryGetValue(IMessage message, int fieldNumber, out int value)
        {
            value = 0;
            if (!TryGetFieldObject(message, fieldNumber, out object field))
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
                Log.Exception(ex, "读取未知字段失败（字段号 " + fieldNumber + "）");
                return false;
            }
        }

        public static bool HasValue(IMessage message, int fieldNumber)
        {
            return TryGetFieldObject(message, fieldNumber, out _);
        }

        /// <summary>清掉某个未知字段（下一次 SetValue 就会重新写）。</summary>
        public static bool ClearValue(IMessage message, int fieldNumber)
        {
            if (message == null)
            {
                return false;
            }

            try
            {
                FieldInfo setField = SetFieldOf(message.GetType());
                FieldInfo fieldsField = FieldsFieldOf(message.GetType());
                if (setField == null || fieldsField == null)
                {
                    return false;
                }

                object set = setField.GetValue(message);
                if (set != null && fieldsField.GetValue(set) is IDictionary fields)
                {
                    fields.Remove(fieldNumber);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "清理未知字段失败（字段号 " + fieldNumber + "）");
            }

            return false;
        }

        private static bool TryGetFieldObject(IMessage message, int fieldNumber, out object field)
        {
            field = null;
            if (message == null)
            {
                return false;
            }

            try
            {
                Type type = message.GetType();
                FieldInfo setField = SetFieldOf(type);
                FieldInfo fieldsField = FieldsFieldOf(type);
                if (setField == null || fieldsField == null)
                {
                    return false;
                }

                object set = setField.GetValue(message);
                if (set == null || !(fieldsField.GetValue(set) is IDictionary fields) || !fields.Contains(fieldNumber))
                {
                    return false;
                }

                field = fields[fieldNumber];
                return field != null;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "读取未知字段容器失败（字段号 " + fieldNumber + "）");
                return false;
            }
        }

        private static FieldInfo SetFieldOf(Type type)
        {
            if (SetFieldCache.TryGetValue(type, out FieldInfo cached))
            {
                return cached;
            }

            FieldInfo found = type.GetField("_unknownFields", BindingFlags.Instance | BindingFlags.NonPublic);
            SetFieldCache[type] = found;
            return found;
        }

        private static FieldInfo FieldsFieldOf(Type type)
        {
            if (FieldsFieldCache.TryGetValue(type, out FieldInfo cached))
            {
                return cached;
            }

            FieldInfo setField = SetFieldOf(type);
            FieldInfo found = setField?.FieldType.GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldsFieldCache[type] = found;
            return found;
        }

        private static MethodInfo GetOrAddFieldOf(Type type)
        {
            if (GetOrAddFieldCache.TryGetValue(type, out MethodInfo cached))
            {
                return cached;
            }

            FieldInfo setField = SetFieldOf(type);
            MethodInfo found = setField?.FieldType.GetMethod("GetOrAddField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GetOrAddFieldCache[type] = found;
            return found;
        }
    }
}
