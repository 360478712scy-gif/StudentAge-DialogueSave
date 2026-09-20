using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Only Capture/plan construction touches active configuration. Each instance exclusively owns
    // validated field-only DTO clones and their mutable containers. No Unity/getter code runs in workers.
    internal sealed class ConfigFingerprintSnapshot
    {
        struct Row { internal object Key, Value; internal string Order; internal List<List<float>> NormalizedRoles; internal Func<object,object,bool> Compare; }
        sealed class Table { internal string Name; internal Row[] Rows; internal Dictionary<object, Row> ByKey; internal Func<object,bool> CompareMap; }
        readonly Table[] tables;
        volatile string digest;
        internal string Digest => digest;
        readonly IContractResolver resolver;
        ConfigFingerprintSnapshot(Table[] tables) { this.tables = tables; resolver = new FrozenResolver(Contracts); }
        sealed class FrozenResolver : IContractResolver
        {
            readonly Dictionary<Type, JsonContract> contracts;
            internal FrozenResolver(Dictionary<Type, JsonContract> source) { contracts = new Dictionary<Type, JsonContract>(source); }
            public JsonContract ResolveContract(Type type) => contracts.TryGetValue(type, out var contract) ? contract : throw Unsupported(type, "后台未预检类型");
        }
        // Built/validated on the main thread: even attribute constructors in this game can call
        // DescCtrl/Cfg. A worker must never invoke DefaultContractResolver's reflection fallback.
        static readonly DefaultContractResolver SchemaResolver = new DefaultContractResolver();
        static readonly Dictionary<Type, JsonContract> Contracts = new Dictionary<Type, JsonContract>();
        static readonly PropertyInfo[] Maps = typeof(Cfg).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.Name.EndsWith("CfgMap", StringComparison.Ordinal)).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        // These plans describe type shape only, never cached configuration values. Main-thread only.
        static readonly Dictionary<Type, Func<object, object>> Plans = new Dictionary<Type, Func<object, object>>();
        static Func<object, object, bool> canonicalTalkComparison;
        static readonly Dictionary<Type, Func<object, object, bool>> Comparisons = new Dictionary<Type, Func<object, object, bool>>();
        static readonly HashSet<Type> Building = new HashSet<Type>();
        static readonly MethodInfo ShallowClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);

        static int warmedMapCount;
        internal static bool PlansReady => warmedMapCount == Maps.Length;

        static void WarmMap(PropertyInfo property, JsonSerializer serializer)
        {
            Type map = property.PropertyType;
            if (!map.IsGenericType || map.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                throw Unsupported(map, "配置表声明类型");
            Type[] types = map.GetGenericArguments();
            if (!Immutable(types[0])) throw Unsupported(types[0], "可变配置键");
            Plan(types[0], serializer);
            Plan(types[1], serializer);
            Comparison(types[1]);
            if (types[1] == typeof(TalkCfg)) Comparison(types[1], true);
        }
        internal static void WarmPlans(JsonSerializer serializer)
        {
            while (!PlansReady) { WarmMap(Maps[warmedMapCount], serializer); warmedMapCount++; }
        }
        internal static bool WarmPlansTick(JsonSerializer serializer)
        {
            var time = System.Diagnostics.Stopwatch.StartNew();
            int count = 0;
            // A single expression compilation is indivisible. Never start another table after
            // the 3ms budget, and cap a tick at two tables even when plans are inexpensive.
            while (!PlansReady && count < 2 && (count == 0 || time.Elapsed.TotalMilliseconds < 3.0))
            {
                WarmMap(Maps[warmedMapCount], serializer); warmedMapCount++; count++;
            }
            return PlansReady;
        }

        internal static ConfigFingerprintSnapshot Capture(JsonSerializer serializer, Action<string, double> diagnostic = null)
        {
            var plansTime = System.Diagnostics.Stopwatch.StartNew();
            WarmPlans(serializer);
            diagnostic?.Invoke("config.plans", plansTime.Elapsed.TotalMilliseconds);
            var copyTime = System.Diagnostics.Stopwatch.StartNew();
            var result = new Table[Maps.Length];
            for (int i = 0; i < Maps.Length; i++)
            {
                var tableTime = System.Diagnostics.Stopwatch.StartNew();
                var table = result[i] = new Table { Name = Maps[i].Name };
                if (!(Maps[i].GetValue(null, null) is IDictionary map)) continue;
                table.Rows = new Row[map.Count]; table.ByKey = new Dictionary<object, Row>(map.Count); int index = 0;
                foreach (DictionaryEntry entry in map)
                {
                    if (entry.Key == null || !Immutable(entry.Key.GetType())) throw Unsupported(entry.Key?.GetType(), "可变配置键");
                    Plan(entry.Key.GetType(), serializer);
                    object copy = entry.Value == null ? null : Plan(entry.Value.GetType(), serializer)(entry.Value);
                    List<List<float>> normalizedRoles = null;
                    if (copy is TalkCfg talk && talk.roles != null && talk.roles.Count > 0)
                    {
                        // Keep raw field order for exact change detection. Canonical roles share
                        // only already-detached inner lists and are solely used by the JSON writer.
                        normalizedRoles = new List<List<float>>(talk.roles);
                        normalizedRoles.Sort((a, b) => {
                            if ((int)a[0] != (int)b[0]) return 0;
                            if (Cfg.TalkAnimeCfgMap[(int)a[1]].type == 1) return -1;
                            return Cfg.TalkAnimeCfgMap[(int)b[1]].type == 1 ? 1 : 0;
                        });
                    }
                    var row = new Row { Key = entry.Key, Value = copy, Order = Convert.ToString(entry.Key), NormalizedRoles = normalizedRoles, Compare=copy==null?null:Comparison(copy.GetType()) };
                    table.Rows[index++] = row; table.ByKey.Add(entry.Key, row);
                }
                table.CompareMap=(Func<object,bool>)typeof(ConfigFingerprintSnapshot).GetMethod(nameof(MapComparison),BindingFlags.NonPublic|BindingFlags.Static)
                    .MakeGenericMethod(Maps[i].PropertyType.GetGenericArguments()).Invoke(null,new object[]{table});
                if (tableTime.Elapsed.TotalMilliseconds >= 10)
                    diagnostic?.Invoke("config.table." + table.Name + "[rows=" + table.Rows.Length + "]", tableTime.Elapsed.TotalMilliseconds);
            }
            var snapshot = new ConfigFingerprintSnapshot(result);
            diagnostic?.Invoke("config.copy", copyTime.Elapsed.TotalMilliseconds);
            return snapshot;
        }

        static bool Immutable(Type type)
        {
            Type nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) return Immutable(nullable);
            return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(char) ||
                type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(TimeSpan);
        }
        static InvalidDataException Unsupported(Type type, string detail) => new InvalidDataException("配置类型未适配：" + (type?.FullName ?? "null") + "（" + detail + "）");
        static Func<object, object> Plan(Type type, JsonSerializer serializer)
        {
            if (Plans.TryGetValue(type, out var existing)) return existing;
            JsonContract contract = SchemaResolver.ResolveContract(type);
            if (contract.Converter != null || contract.IsReference == true || contract.OnSerializingCallbacks.Count != 0 ||
                contract.OnSerializedCallbacks.Count != 0 || contract.OnErrorCallbacks.Count != 0)
                throw Unsupported(type, "自定义序列化行为");
            if (Immutable(type))
            {
                Type underlying = Nullable.GetUnderlyingType(type);
                if (underlying != null) Plan(underlying, serializer);
                Contracts[type] = contract;
                Func<object, object> identity = value => value;
                Plans[type] = identity;
                return identity;
            }
            if (Building.Count >= 64) throw Unsupported(type, "类型嵌套过深");
            if (!Building.Add(type)) throw Unsupported(type, "循环类型");
            try
            {
                Func<object, object> clone;
                if (type.IsArray && type.GetArrayRank() == 1)
                    clone = ContainerPlan("ArrayPlan", type.GetElementType(), null, serializer);
                else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                    clone = ContainerPlan("ListPlan", type.GetGenericArguments()[0], null, serializer);
                else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    Type[] arguments = type.GetGenericArguments();
                    if (!Immutable(arguments[0])) throw Unsupported(type, "可变字典键");
                    clone = ContainerPlan("DictionaryPlan", arguments[0], arguments[1], serializer);
                }
                else
                {
                    if (type.IsValueType || type.Namespace != "Config") throw Unsupported(type, "非配置数据类");
                    if (!(contract is JsonObjectContract obj) || obj.ItemConverter != null || obj.ItemIsReference == true || obj.ExtensionDataGetter != null)
                        throw Unsupported(type, "自定义序列化行为");
                    var fields = new List<FieldInfo>();
                    for (Type owner = type; owner != null && owner != typeof(object); owner = owner.BaseType)
                    {
                        if (owner.Namespace != "Config") throw Unsupported(type, "外部基类");
                        fields.AddRange(owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                    }
                    foreach (JsonProperty property in obj.Properties)
                    {
                        if (property.Ignored || !property.Readable) continue;
                        if (property.Converter != null || property.ItemConverter != null || property.IsReference == true || property.ItemIsReference == true || property.ShouldSerialize != null || property.GetIsSpecified != null ||
                            !fields.Any(f => f.Name == property.UnderlyingName && f.DeclaringType == property.DeclaringType))
                            throw Unsupported(type, "非纯字段成员 " + property.PropertyName);
                    }
                    var source = Expression.Parameter(typeof(object), "source");
                    var copy = Expression.Variable(type, "copy");
                    var statements = new List<Expression> {
                        Expression.Assign(copy, Expression.Convert(Expression.Call(source, ShallowClone), type))
                    };
                    foreach (FieldInfo field in fields)
                    {
                        var child = Plan(field.FieldType, serializer);
                        if (Immutable(field.FieldType)) continue;
                        if (field.IsInitOnly) throw Unsupported(type, "只读可变字段 " + field.Name);
                        Expression fieldValue = Expression.Field(copy, field);
                        statements.Add(Expression.Assign(fieldValue, Expression.Convert(Expression.Invoke(Expression.Constant(child), Expression.Convert(fieldValue, typeof(object))), field.FieldType)));
                    }
                    statements.Add(Expression.Convert(copy, typeof(object)));
                    var body = Expression.Condition(Expression.Equal(source, Expression.Constant(null)), Expression.Constant(null, typeof(object)), Expression.Block(new[] { copy }, statements));
                    clone = Expression.Lambda<Func<object, object>>(body, source).Compile();
                    // Exact runtime type prevents a derived instance from bringing unvalidated fields/getters.
                    var compiled = clone;
                    clone = value => value == null ? null : value.GetType() == type ? compiled(value) : throw Unsupported(value.GetType(), "配置字段运行时类型不匹配");
                }
                Contracts[type] = contract;
                Plans.Add(type, clone);
                return clone;
            }
            finally { Building.Remove(type); }
        }
        static Func<object, object> ContainerPlan(string method, Type first, Type second, JsonSerializer serializer)
        {
            Type element = second ?? first;
            Plan(first, serializer);
            Func<object, object> child = Plan(element, serializer);
            if (Immutable(element)) child = null;
            return (Func<object, object>)typeof(ConfigFingerprintSnapshot).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(second == null ? new[] { first } : new[] { first, second }).Invoke(null, new object[] { child });
        }
        static Func<object, object> ListPlan<T>(Func<object, object> child) => value =>
        {
            if (value == null) return null;
            if (value.GetType() != typeof(List<T>)) throw Unsupported(value.GetType(), "列表派生类型");
            var source = (List<T>)value;
            if (child == null) return new List<T>(source); // Copies primitive/string data without boxing each item.
            var copy = new List<T>(source.Count);
            foreach (T item in source) copy.Add((T)child(item));
            return copy;
        };
        static Func<object, object> ArrayPlan<T>(Func<object, object> child) => value =>
        {
            if (value == null) return null;
            if (value.GetType() != typeof(T[])) throw Unsupported(value.GetType(), "数组运行时类型");
            var source = (T[])value; var copy = (T[])source.Clone();
            if (child != null) for (int i = 0; i < copy.Length; i++) copy[i] = (T)child(source[i]);
            return copy;
        };
        static Func<object, object> DictionaryPlan<TKey, TValue>(Func<object, object> child) => value =>
        {
            if (value == null) return null;
            if (value.GetType() != typeof(Dictionary<TKey, TValue>)) throw Unsupported(value.GetType(), "字典派生类型");
            var source = (Dictionary<TKey, TValue>)value;
            // Never retain a custom comparer supplied by another plugin in the detached graph.
            if (!ReferenceEquals(source.Comparer, EqualityComparer<TKey>.Default)) throw Unsupported(value.GetType(), "自定义字典比较器");
            if (child == null) return new Dictionary<TKey, TValue>(source);
            var copy = new Dictionary<TKey, TValue>(source.Count);
            foreach (var entry in source) copy.Add(entry.Key, (TValue)child(entry.Value));
            return copy;
        };

        // Full main-thread value comparison. No reference/version shortcut can hide an in-place
        // edit. The detached side is immutable even while a worker serializes it.
        internal bool MatchesCurrent()
        {
            for (int i = 0; i < Maps.Length; i++)
            {
                IDictionary map = Maps[i].GetValue(null, null) as IDictionary;
                Table table = tables[i];
                if (map == null) { if (table.Rows != null) return false; continue; }
                if (table.Rows == null || map.Count != table.Rows.Length) return false;
                if(!table.CompareMap(map))return false;
            }
            return true;
        }

        static Func<object,bool> MapComparison<TKey,TValue>(Table table)
        {
            var expected=table.Rows.ToDictionary(r=>(TKey)r.Key,r=>r);
            return value=>
            {
                if(!(value is Dictionary<TKey,TValue> map) || map.Count!=expected.Count)return false;
                foreach(var pair in map)
                {
                    if(!expected.TryGetValue(pair.Key,out var row) || !KeyOrder<TKey>.Matches(pair.Key,row.Order))return false;
                    object current=pair.Value;
                    if(current==null || row.Value==null){if(current!=null || row.Value!=null)return false;continue;}
                    if(row.Compare(current,row.Value))continue;
                    if(current.GetType()!=typeof(TalkCfg) || row.Value.GetType()!=typeof(TalkCfg) || !canonicalTalkComparison(current,row.Value))return false;
                    var talk=(TalkCfg)current;var roles=talk.roles;
                    if(roles!=null && roles.Count>0)
                    {
                        roles=new List<List<float>>(roles);
                        roles.Sort((a,b)=>{
                            if((int)a[0]!=(int)b[0])return 0;
                            if(Cfg.TalkAnimeCfgMap[(int)a[1]].type==1)return -1;
                            return Cfg.TalkAnimeCfgMap[(int)b[1]].type==1?1:0;
                        });
                    }
                    if(!Comparison(typeof(List<List<float>>))(roles,row.NormalizedRoles??((TalkCfg)row.Value).roles))return false;
                }
                return true;
            };
        }
        static class KeyOrder<TKey>
        {
            internal static readonly Func<TKey,string,bool> Matches=Build();
            static Func<TKey,string,bool> Build()
            {
                // Dictionary lookup already proves the exact key. Positive integer
                // and string formatting cannot vary by culture; avoid allocating their
                // identical strings for every row. Negative/other keys retain legacy formatting.
                if(typeof(TKey)==typeof(int))return (Func<TKey,string,bool>)(object)(Func<int,string,bool>)
                    ((key,order)=>key>=0 || string.Equals(Convert.ToString(key),order,StringComparison.Ordinal));
                if(typeof(TKey)==typeof(string))return (key,order)=>true;
                return (key,order)=>string.Equals(Convert.ToString(key),order,StringComparison.Ordinal);
            }
        }
        // Choose scalar semantics once per closed type, rather than repeated type tests
        // and boxing on every item in every configuration list on Mono.
        static class ScalarComparer<T>
        {
            internal static readonly Func<T,T,bool> Equal=Build();
            static Func<T,T,bool> Build()
            {
                if(typeof(T)==typeof(float))return (Func<T,T,bool>)(object)(Func<float,float,bool>)((a,b)=>FloatBits(a)==FloatBits(b));
                if(typeof(T)==typeof(double))return (Func<T,T,bool>)(object)(Func<double,double,bool>)((a,b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b));
                if(typeof(T)==typeof(decimal) || typeof(T)==typeof(DateTime) || typeof(T)==typeof(DateTimeOffset) || Nullable.GetUnderlyingType(typeof(T))!=null)return ScalarEqual;
                return EqualityComparer<T>.Default.Equals;
            }
        }

        static IEnumerable<FieldInfo> Fields(Type type)
        {
            for (Type owner = type; owner != null && owner != typeof(object); owner = owner.BaseType)
                foreach (FieldInfo field in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) yield return field;
        }
        static Func<object, object, bool> Comparison(Type type, bool ignoreTalkRoles = false)
        {
            if (ignoreTalkRoles && canonicalTalkComparison != null) return canonicalTalkComparison;
            if (!ignoreTalkRoles && Comparisons.TryGetValue(type, out var existing)) return existing;
            if (!Plans.ContainsKey(type)) throw Unsupported(type, "比较类型未预检");
            Func<object, object, bool> compare;
            if (Immutable(type)) compare = ScalarComparison(type);
            else if (type.IsArray) compare = ContainerComparison("CompareArray", type.GetElementType());
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) compare = ContainerComparison("CompareList", type.GetGenericArguments());
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) compare = ContainerComparison("CompareDictionary", type.GetGenericArguments());
            else
            {
                var left = Expression.Parameter(typeof(object), "left"); var right = Expression.Parameter(typeof(object), "right");
                var typedLeft=Expression.Variable(type,"a");var typedRight=Expression.Variable(type,"b");
                Expression result = Expression.Constant(true);
                foreach (FieldInfo field in Fields(type))
                {
                    if (ignoreTalkRoles && type == typeof(TalkCfg) && field.Name == "roles") continue;
                    var a = Expression.Field(typedLeft, field); var b = Expression.Field(typedRight, field);
                    Expression equality;
                    // Emit direct scalar comparisons for exact, non-floating types.
                    // Mono otherwise repeats many generic type tests for every scalar
                    // in every configuration row; the comparison scope is unchanged.
                    if (field.FieldType==typeof(string) || field.FieldType.IsEnum ||
                        (field.FieldType.IsPrimitive && field.FieldType!=typeof(float) && field.FieldType!=typeof(double)))
                        equality=Expression.Equal(a,b);
                    else if(field.FieldType==typeof(float))
                        equality=Expression.Equal(Expression.Call(typeof(ConfigFingerprintSnapshot).GetMethod(nameof(FloatBits),BindingFlags.NonPublic|BindingFlags.Static),a),Expression.Call(typeof(ConfigFingerprintSnapshot).GetMethod(nameof(FloatBits),BindingFlags.NonPublic|BindingFlags.Static),b));
                    else if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition()==typeof(List<>))
                        equality=ListExpression(field.FieldType,a,b);
                    else if (Immutable(field.FieldType))
                        equality = Expression.Call(typeof(ConfigFingerprintSnapshot).GetMethod(nameof(ScalarEqual), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(field.FieldType), a, b);
                    else
                    {
                        var bothNull=Expression.AndAlso(Expression.Equal(a,Expression.Constant(null,field.FieldType)),Expression.Equal(b,Expression.Constant(null,field.FieldType)));
                        equality=Expression.OrElse(bothNull,Expression.Invoke(Expression.Constant(Comparison(field.FieldType)),Expression.Convert(a,typeof(object)),Expression.Convert(b,typeof(object))));
                    }
                    result = Expression.AndAlso(result, equality);
                }
                var compiled = Expression.Lambda<Func<object, object, bool>>(Expression.Block(new[]{typedLeft,typedRight},Expression.Assign(typedLeft,Expression.Convert(left,type)),Expression.Assign(typedRight,Expression.Convert(right,type)),result), left, right).Compile();
                compare = (a, b) => a == null || b == null ? a == null && b == null : a.GetType() == type && b.GetType() == type && compiled(a, b);
            }
            if (ignoreTalkRoles) canonicalTalkComparison = compare; else Comparisons.Add(type, compare);
            return compare;
        }
        static Expression ListExpression(Type type,Expression a,Expression b)
        {
            var x=Expression.Variable(type,"xs");var y=Expression.Variable(type,"ys");
            var i=Expression.Variable(typeof(int),"i");var end=Expression.Label(typeof(bool),"equal");
            var count=Expression.Property(x,"Count");
            var left=Expression.Property(x,"Item",i);var right=Expression.Property(y,"Item",i);
            Type element=type.GetGenericArguments()[0];Expression item;
            if(element==typeof(string) || element.IsEnum || (element.IsPrimitive && element!=typeof(float) && element!=typeof(double)))item=Expression.Equal(left,right);
            else if(element==typeof(float))item=Expression.Equal(Expression.Call(typeof(ConfigFingerprintSnapshot).GetMethod(nameof(FloatBits),BindingFlags.NonPublic|BindingFlags.Static),left),Expression.Call(typeof(ConfigFingerprintSnapshot).GetMethod(nameof(FloatBits),BindingFlags.NonPublic|BindingFlags.Static),right));
            else if(element.IsGenericType && element.GetGenericTypeDefinition()==typeof(List<>))item=ListExpression(element,left,right);
            else item=Expression.Invoke(Expression.Constant(Comparison(element)),Expression.Convert(left,typeof(object)),Expression.Convert(right,typeof(object)));
            var nullValue=Expression.Constant(null,type);
            return Expression.Block(new[]{x,y,i},Expression.Assign(x,a),Expression.Assign(y,b),
                Expression.Condition(Expression.OrElse(Expression.Equal(x,nullValue),Expression.Equal(y,nullValue)),
                    Expression.AndAlso(Expression.Equal(x,nullValue),Expression.Equal(y,nullValue)),
                    Expression.AndAlso(Expression.AndAlso(Expression.TypeEqual(x,type),Expression.TypeEqual(y,type)),
                        Expression.AndAlso(Expression.Equal(count,Expression.Property(y,"Count")),
                            Expression.Block(Expression.Assign(i,Expression.Constant(0)),
                                Expression.Loop(Expression.Block(
                                    Expression.IfThen(Expression.GreaterThanOrEqual(i,count),Expression.Break(end,Expression.Constant(true))),
                                    Expression.IfThen(Expression.Not(item),Expression.Break(end,Expression.Constant(false))),
                                    Expression.PostIncrementAssign(i)),end))))));
        }
        static class ScalarType<T> { internal static readonly Type Underlying = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T); }
        static bool ScalarEqual<T>(T left, T right)
        {
            if (ScalarType<T>.Underlying != typeof(T))
            {
                object a = left, b = right;
                if (a == null || b == null) return a == null && b == null;
                if (ScalarType<T>.Underlying == typeof(float)) return FloatBits((float)a) == FloatBits((float)b);
                if (ScalarType<T>.Underlying == typeof(double)) return BitConverter.DoubleToInt64Bits((double)a) == BitConverter.DoubleToInt64Bits((double)b);
                if (ScalarType<T>.Underlying == typeof(decimal)) return Enumerable.SequenceEqual(decimal.GetBits((decimal)a), decimal.GetBits((decimal)b));
                if (ScalarType<T>.Underlying == typeof(DateTime)) return ((DateTime)a).ToBinary() == ((DateTime)b).ToBinary();
                if (ScalarType<T>.Underlying == typeof(DateTimeOffset)) return ((DateTimeOffset)a).EqualsExact((DateTimeOffset)b);
            }
            // EqualityComparer treats signed zero as equal, but JSON preserves its sign.
            // Bit checks also detect NaN payload edits (conservative, never a stale-cache hit).
            if (typeof(T) == typeof(float)) return FloatBits((float)(object)left) == FloatBits((float)(object)right);
            if (typeof(T) == typeof(double)) return BitConverter.DoubleToInt64Bits((double)(object)left) == BitConverter.DoubleToInt64Bits((double)(object)right);
            if (typeof(T) == typeof(decimal)) return Enumerable.SequenceEqual(decimal.GetBits((decimal)(object)left), decimal.GetBits((decimal)(object)right));
            if (typeof(T) == typeof(DateTime)) return ((DateTime)(object)left).ToBinary() == ((DateTime)(object)right).ToBinary();
            if (typeof(T) == typeof(DateTimeOffset)) return ((DateTimeOffset)(object)left).EqualsExact((DateTimeOffset)(object)right);
            return EqualityComparer<T>.Default.Equals(left, right);
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct FloatUnion { [System.Runtime.InteropServices.FieldOffset(0)] internal float Value; [System.Runtime.InteropServices.FieldOffset(0)] internal int Bits; }
        static int FloatBits(float value) => new FloatUnion { Value = value }.Bits;
        static Func<object, object, bool> ScalarComparison(Type type)
        {
            var a = Expression.Parameter(typeof(object)); var b = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, object, bool>>(Expression.Call(typeof(ConfigFingerprintSnapshot).GetMethod(nameof(ScalarEqual), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type), Expression.Convert(a, type), Expression.Convert(b, type)), a, b).Compile();
        }
        static Func<object, object, bool> ContainerComparison(string name, params Type[] types)
        {
            Type value = types[types.Length - 1];
            Func<object, object, bool> child = Immutable(value) ? null : Comparison(value);
            return (Func<object, object, bool>)typeof(ConfigFingerprintSnapshot).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(types).Invoke(null, new object[] { child });
        }
        static Func<object, object, bool> CompareList<T>(Func<object, object, bool> child) => (a, b) =>
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.GetType() != typeof(List<T>) || b.GetType() != typeof(List<T>)) return false;
            var x = (List<T>)a; var y = (List<T>)b;
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++) if (child == null ? !ScalarComparer<T>.Equal(x[i], y[i]) : !child(x[i], y[i])) return false;
            return true;
        };
        static Func<object, object, bool> CompareArray<T>(Func<object, object, bool> child) => (a, b) =>
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.GetType() != typeof(T[]) || b.GetType() != typeof(T[])) return false;
            var x = (T[])a; var y = (T[])b;
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (child == null ? !ScalarComparer<T>.Equal(x[i], y[i]) : !child(x[i], y[i])) return false;
            return true;
        };
        static Func<object, object, bool> CompareDictionary<TKey, TValue>(Func<object, object, bool> child) => (a, b) =>
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.GetType() != typeof(Dictionary<TKey, TValue>) || b.GetType() != typeof(Dictionary<TKey, TValue>)) return false;
            var x = (Dictionary<TKey, TValue>)a; var y = (Dictionary<TKey, TValue>)b;
            if (x.Count != y.Count || !ReferenceEquals(x.Comparer, EqualityComparer<TKey>.Default)) return false;
            using (var xe = x.GetEnumerator()) using (var ye = y.GetEnumerator())
                while (xe.MoveNext())
                {
                    if (!ye.MoveNext() || !ScalarComparer<TKey>.Equal(xe.Current.Key, ye.Current.Key) ||
                        (child == null ? !ScalarComparer<TValue>.Equal(xe.Current.Value, ye.Current.Value) : !child(xe.Current.Value, ye.Current.Value))) return false;
                }
            return true;
        };

        internal string ComputeDigest()
        {
            if (digest != null) return digest;
            // Separate serializer; resolver only serves main-thread-approved pure-field contracts.
            // No fallback reflection/attribute construction and no shared DataJson serializer.
            var serializer = JsonSerializer.Create(new JsonSerializerSettings {
                TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                MaxDepth = 64, ContractResolver = resolver
            });
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var text = new StreamWriter(crypto, new UTF8Encoding(false), 8192, true))
            using (var writer = new JsonTextWriter(text) { CloseOutput = false })
            {
                writer.WriteStartObject();
                foreach (Table table in tables)
                {
                    writer.WritePropertyName(table.Name);
                    if (table.Rows == null) { writer.WriteNull(); continue; }
                    writer.WriteStartArray();
                    foreach (Row row in table.Rows.OrderBy(r => r.Order, StringComparer.Ordinal))
                    {
                        writer.WriteStartArray(); serializer.Serialize(writer, row.Key);
                        if (row.Value is TalkCfg talk && row.NormalizedRoles != null)
                        {
                            JObject token = JObject.FromObject(talk, serializer);
                            token["roles"] = JArray.FromObject(row.NormalizedRoles, serializer);
                            token.WriteTo(writer);
                        }
                        else serializer.Serialize(writer, row.Value);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject(); writer.Flush(); text.Flush(); crypto.FlushFinalBlock();
                digest = BitConverter.ToString(sha.Hash).Replace("-", "");
                return digest;
            }
        }
    }
}
