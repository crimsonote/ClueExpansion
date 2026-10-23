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

        /// <summary>
        /// 「显示时刻」的字段号（秒，覆盖写）。只在**时钟破坏点已破坏期间**写进去，
        /// 由出站口（`Server.Game.HostPeerSession.Send`）把线索的 `Time` 换成它。
        /// </summary>
        /// <remarks>
        /// <b>为什么不直接改 `Time`</b>：`Time` 参与原版 30 秒去重窗口
        /// （`RecordLastUsingPlayer` 的 `x.Time &gt;= limitTime`，0.1.18 :166405）——
        /// 把它改成更早的冻结时刻，会让"先删后加"的合并**永不命中**，同一台设备的线索会重复追加。
        /// 所以真值保持真实时间，另存一份"该显示成几点"，只在**出站副本**上替换。
        /// <para>
        /// 为什么是 101：100 已被「详细活动」占用（见 <see cref="WhatFieldNumber"/>），
        /// 101 紧邻它、同样落在没人用的租用区；未装本模块的客户端照旧忽略它。
        /// </para>
        /// </remarks>
        public const int DisplayTimeFieldNumber = 101;

        /// <summary>写入「显示时刻」（覆盖写，幂等）。</summary>
        public static bool SetDisplayTime(IMessage message, int time)
        {
            return time != 0 && ReplaceValue(message, DisplayTimeFieldNumber, time);
        }

        /// <summary>读取「显示时刻」；0 = 这条线索没有被污染。</summary>
        public static int GetDisplayTime(IMessage message)
        {
            return TryGetValue(message, DisplayTimeFieldNumber, out int value) ? value : 0;
        }

        /// <summary>
        /// 「真实当事人」的字段号：身份污染把线索真值写成 <c>PlayerId = 0</c>（对外「某人」）时，
        /// 把**真正操作的人**记在这里，供出站口按收件人还原 —— 当事人自己看自己的线索应当是实名。
        /// </summary>
        /// <remarks>
        /// ⚠️ 这个字段**只存在于服务端真值**：出站副本上会被清零
        /// （<c>ShadowSabotage/Wire.cs</c> 的 <c>RewriteOutflow</c>），
        /// 否则装了本模块的"别人"可以自己读出当事人是谁。
        /// 与 101 一样落在没人用的租用区（100 = 详细活动、101 = 显示时刻）。
        /// </remarks>
        public const int RealOwnerFieldNumber = 103;

        /// <summary>写入「真实当事人」（覆盖写，幂等；传 0 = 抹掉，出站时用）。</summary>
        public static bool SetRealOwner(IMessage message, int playerId)
        {
            return ReplaceValue(message, RealOwnerFieldNumber, playerId);
        }

        /// <summary>读取「真实当事人」；0 = 没有记录。</summary>
        public static int GetRealOwner(IMessage message)
        {
            return TryGetValue(message, RealOwnerFieldNumber, out int value) ? value : 0;
        }

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

        /// <summary>覆盖写任意字段号（先清掉旧值，避开未知字段 varintList 的追加语义）。</summary>
        public static bool ReplaceValue(IMessage message, int fieldNumber, int value)
        {
            if (message == null || value == 0)
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
                if (!(fieldsField.GetValue(set) is IDictionary fields))
                {
                    return false;
                }

                // ⚠️ 2026-10-20：**不能**写成 ClearValue + SetValue。
                // 离线往返实测：同一字段第二次调用时，ClearValue 移除条目后 SetValue 的
                // GetOrAddField 写入会失效，读回 0（第一次写却正常）。
                // 这就是「线索第一次记录能冻结、合并刷新后时间又变回真实」的根因。
                // 这里直接构造一个新的 UnknownField 覆盖旧条目，一步到位。
                Type fieldType = getOrAdd.ReturnType;
                object field = Activator.CreateInstance(fieldType, true);
                MethodInfo addVarint = fieldType.GetMethod(
                    "AddVarint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (addVarint == null)
                {
                    return false;
                }

                addVarint.Invoke(field, new object[] { (ulong)value });
                fields[fieldNumber] = field;
                setField.SetValue(message, set);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "覆盖未知字段失败（字段号 " + fieldNumber + "）");
                return false;
            }
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