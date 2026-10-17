using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Google.Protobuf;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 在 <see cref="PropositionInfo"/> 的 protobuf 未知字段区读写「详细活动」。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条通道用在这里</b>：<c>what</c> 本质上是**线索的一个属性**。
    /// 让它和 <c>PlayerId/Time/RoomId</c> 躺在同一个对象里，就不需要关联键、
    /// 不需要保证"详情包先到"的时序、也不会出现"同房间同时刻两条线索撞键"。
    /// 而握手与开关状态是**独立事件**（没有宿主对象可附着），那些走自定义协议
    /// （<see cref="Net.CdWire"/>）。
    /// <para>
    /// 机制：0.1.18 删除了 <c>PropositionInfo.WhatType</c>，protobuf 的 wire 规则要求
    /// 解析方原样保留不认识的字段，所以：
    /// </para>
    /// <list type="bullet">
    /// <item>服务端把活动写进未知字段 → 随线索包一起序列化下发；</item>
    /// <item>0.1.18 原版客户端/服务端解析时把它丢进自己的 <c>_unknownFields</c> 并忽略 ——
    /// **不报错、不改变任何原版行为**；</item>
    /// <item>装了本模块的客户端读回来。注意 <c>PropositionInfo.Clone()</c> 会一并复制未知字段，
    /// 所以广播与扫描两条路径都带得上（写必须在 <c>Clone()</c> **之前**，见
    /// <c>Features/Host/RecordDetail</c> 的三个钩子）。</item>
    /// </list>
    /// <para>
    /// <b>实现约束</b>：<c>Google.Protobuf.UnknownFieldSet</c> 是 public，但它返回的
    /// <c>UnknownField</c> 是 internal，因此只能走反射链
    /// （<c>_unknownFields</c> → <c>fields</c> → <c>GetOrAddField</c> → <c>AddVarint</c>，
    /// 读取时再取 <c>varintList</c>）。这些都是 protobuf 的私有实现细节，换版本可能断 ——
    /// 所以启动自检会**真的写一次再读一次**来验证（<c>Core/Diagnostics.cs</c>），
    /// 断了会在日志里明说，而不是静默失效。
    /// </para>
    /// </remarks>
    internal static class UnknownFields
    {
        /// <summary>
        /// 详细活动（EWhatType）的字段号，载体是 PropositionInfo。
        /// </summary>
        /// <remarks>
        /// 刻意避开 4 和 6：4 是 0.1.18 刚弃用的号（官方随时可能回收给新字段），
        /// 6 紧邻 <c>Destroyed=5</c>（"下一个新字段"最可能挑的号）。
        /// 100 落在明显没人用的租用区。
        /// </remarks>
        public const int WhatFieldNumber = 100;

        private static readonly Dictionary<Type, FieldInfo> SetFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> FieldsFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, MethodInfo> GetOrAddFieldCache = new Dictionary<Type, MethodInfo>();

        public static bool SetWhat(IMessage message, int what)
        {
            return what != 0 && SetValue(message, WhatFieldNumber, what);
        }

        public static int GetWhat(IMessage message)
        {
            return TryGetValue(message, WhatFieldNumber, out int value) ? value : 0;
        }

        /// <summary>
        /// 覆盖写（先清掉旧值）。
        /// </summary>
        /// <remarks>
        /// <see cref="SetWhat"/> 是幂等的（未知字段的 varintList 是**追加**语义，重复写会变成两个值），
        /// 但伪证界面里玩家会反复改选，那种场景需要真正覆盖，所以另外提供这一条。
        /// </remarks>
        public static bool ReplaceWhat(IMessage message, int what)
        {
            if (message == null || what == 0)
            {
                return false;
            }

            ClearValue(message, WhatFieldNumber);
            return SetValue(message, WhatFieldNumber, what);
        }

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

        /// <summary>清掉某个未知字段（下一次 SetValue 会重新写）。</summary>
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
