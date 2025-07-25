﻿using FreeSql;
using FreeSql.DataAnnotations;
using FreeSql.Internal.CommonProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

public static class FreeSqlJsonMapCoreExtensions
{
    static int _isAoped = 0;
    static ConcurrentDictionary<Type, bool> _dicTypes = new ConcurrentDictionary<Type, bool>();
    static MethodInfo MethodJsonConvertDeserializeObject = typeof(JsonConvert).GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type), typeof(JsonSerializerSettings) });
    static MethodInfo MethodJsonConvertSerializeObject = typeof(JsonConvert).GetMethod("SerializeObject", new[] { typeof(object), typeof(JsonSerializerSettings) });
    static ConcurrentDictionary<Type, ConcurrentDictionary<string, bool>> _dicJsonMapFluentApi = new ConcurrentDictionary<Type, ConcurrentDictionary<string, bool>>();
    static object _concurrentObj = new object();

    public static ColumnFluent JsonMap(this ColumnFluent col)
    {
        _dicJsonMapFluentApi.GetOrAdd(col._entityType, et => new ConcurrentDictionary<string, bool>())
            .GetOrAdd(col._property.Name, pn => true);
        return col;
    }

    /// <summary>
    /// When the entity class property is <see cref="object"/> and the attribute is marked as <see cref="JsonMapAttribute"/>, map storage in JSON format. <br />
    /// 当实体类属性为【对象】时，并且标记特性 [JsonMap] 时，该属性将以JSON形式映射存储
    /// </summary>
    /// <returns></returns>
    public static void UseJsonMap(this IFreeSql fsql)
    {
        UseJsonMap(fsql, JsonConvert.DefaultSettings?.Invoke() ?? new JsonSerializerSettings());
    }

    public static void UseJsonMap(this IFreeSql fsql, JsonSerializerSettings settings)
    {
        if (Interlocked.CompareExchange(ref _isAoped, 1, 0) == 0)
        {
            FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionSwitchTypeFullName.Add((LabelTarget returnTarget, Expression valueExp, Type type) =>
            {
                if (_dicTypes.ContainsKey(type)) return Expression.IfThenElse(
                    Expression.TypeIs(valueExp, type),
                    Expression.Return(returnTarget, valueExp),
                    Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodJsonConvertDeserializeObject, Expression.Convert(valueExp, typeof(string)), Expression.Constant(type),Expression.Constant(settings)), type))
                );
                return null;
            });
        }

        fsql.Aop.ConfigEntityProperty += (s, e) =>
        {
            var isJsonMap = e.Property.GetCustomAttributes(typeof(JsonMapAttribute), false).Any() || _dicJsonMapFluentApi.TryGetValue(e.EntityType, out var tryjmfu) && tryjmfu.ContainsKey(e.Property.Name);
            if (isJsonMap)
            {
                if (_dicTypes.ContainsKey(e.Property.PropertyType) == false &&
                    FreeSql.Internal.Utils.dicExecuteArrayRowReadClassOrTuple.ContainsKey(e.Property.PropertyType))
                    return; //基础类型使用 JsonMap 无效

                if (e.ModifyResult.MapType == null)
                {
                    switch (fsql.Ado.DataType)
                    {
                        case DataType.PostgreSQL:
                            e.ModifyResult.MapType = typeof(JObject);
                            break;
                        default:
                            e.ModifyResult.MapType = typeof(string);
                            e.ModifyResult.StringLength = -2;
                            break;
                    }
                }
                if (_dicTypes.TryAdd(e.Property.PropertyType, true))
                {
                    lock (_concurrentObj)
                    {
                        FreeSql.Internal.Utils.dicExecuteArrayRowReadClassOrTuple[e.Property.PropertyType] = true;
                        FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionObjectToStringIfThenElse.Add((LabelTarget returnTarget, Expression valueExp, Expression elseExp, Type type) =>
                        {
                            return Expression.IfThenElse(
                                Expression.TypeIs(valueExp, e.Property.PropertyType),
                                Expression.Return(returnTarget, Expression.Call(MethodJsonConvertSerializeObject, Expression.Convert(valueExp, typeof(object)), Expression.Constant(settings)), typeof(object)),
                                elseExp);
                        });
                    }
                }
            }
        };
        switch (fsql.Ado.DataType)
        {
            case DataType.Sqlite:
            case DataType.MySql:
            case DataType.OdbcMySql:
            case DataType.CustomMySql:
            case DataType.SqlServer:
            case DataType.OdbcSqlServer:
            case DataType.CustomSqlServer:
            case DataType.Oracle:
            case DataType.OdbcOracle:
            case DataType.CustomOracle:
            case DataType.Dameng:
            case DataType.DuckDB:
            case DataType.PostgreSQL:
            case DataType.OdbcPostgreSQL:
            case DataType.CustomPostgreSQL:
            case DataType.KingbaseES:
            case DataType.ShenTong:
                fsql.Aop.ParseExpression += (_, e) =>
                {
                    //if (e.Expression is MethodCallExpression callExp)
                    //{
                    //    var objExp = callExp.Object;
                    //    var objType = objExp?.Type;
                    //    if (objType?.FullName == "System.Byte[]") return;

                    //    if (objType == null && callExp.Method.DeclaringType == typeof(Enumerable))
                    //    {
                    //        objExp = callExp.Arguments.FirstOrDefault();
                    //        objType = objExp?.Type;
                    //    }
                    //    if (objType == null) objType = callExp.Method.DeclaringType;
                    //    if (objType != null || objType.IsArrayOrList())
                    //    {
                    //        string left = null;
                    //        switch (callExp.Method.Name)
                    //        {
                    //            case "Any":
                    //                left = objExp == null ? null : getExp(objExp);
                    //                if (left.StartsWith("(") || left.EndsWith(")")) left = $"array[{left.TrimStart('(').TrimEnd(')')}]";
                    //                return $"(case when {left} is null then 0 else array_length({left},1) end > 0)";
                    //            case "Contains":
                    //        }
                    //    }
                    //}
                    //处理 mysql enum -> int
                    switch (fsql.Ado.DataType)
                    {
                        case DataType.MySql:
                        case DataType.OdbcMySql:
                        case DataType.CustomMySql:
                            if (e.Expression.NodeType == ExpressionType.Equal &&
                                e.Expression is BinaryExpression binaryExpression)
                            {
                                var comonExp = (fsql.Select<object>() as Select0Provider)._commonExpression;
                                var leftExp = binaryExpression.Left;
                                var rightExp = binaryExpression.Right;
                                if (
                                   leftExp.NodeType == ExpressionType.Convert &&
                                   leftExp is UnaryExpression leftExpUexp &&
                                   leftExpUexp.Operand?.Type.NullableTypeOrThis().IsEnum == true &&

                                   rightExp.NodeType == ExpressionType.Convert &&
                                   rightExp is UnaryExpression rightExpUexp &&
                                   rightExpUexp.Operand?.Type.NullableTypeOrThis().IsEnum == true)
                                {
                                    string leftSql = null, rightSql = null;
                                    if (leftExpUexp.Operand.NodeType == ExpressionType.MemberAccess &&
                                        LocalParseMemberExp(leftExpUexp.Operand as MemberExpression))
                                        leftSql = e.Result;

                                    if (rightExpUexp.Operand.NodeType == ExpressionType.MemberAccess &&
                                        LocalParseMemberExp(rightExpUexp.Operand as MemberExpression))
                                        rightSql = e.Result;

                                    if (!string.IsNullOrEmpty(leftSql) && string.IsNullOrEmpty(rightSql) && !rightExpUexp.Operand.IsParameter())
                                        rightSql = comonExp.formatSql(Expression.Lambda(rightExpUexp.Operand).Compile().DynamicInvoke(), typeof(int), null, null);
                                    if (string.IsNullOrEmpty(leftSql) && !string.IsNullOrEmpty(rightSql) && !leftExpUexp.Operand.IsParameter())
                                        leftSql = comonExp.formatSql(Expression.Lambda(leftExpUexp.Operand).Compile().DynamicInvoke(), typeof(int), null, null);

                                    if (!string.IsNullOrEmpty(leftSql) && !string.IsNullOrEmpty(rightSql))
                                    {
                                        e.Result = $"{leftSql} = {rightSql}";
                                        return;
                                    }
                                    e.Result = null;
                                    return;
                                }
                            }
                            if (e.Expression.NodeType == ExpressionType.Call &&
                                e.Expression is MethodCallExpression callExp &&
                                callExp.Method.Name == "Contains")
                            {
                                var objExp = callExp.Object;
                                var objType = objExp?.Type;
                                if (objType?.FullName == "System.Byte[]") return;

                                var argIndex = 0;
                                if (objType == null && callExp.Method.DeclaringType == typeof(Enumerable))
                                {
                                    objExp = callExp.Arguments.FirstOrDefault();
                                    objType = objExp?.Type;
                                    argIndex++;

                                    if (objType == typeof(string)) return;
                                }
                                if (objType == null) objType = callExp.Method.DeclaringType;
                                if (objType != null || objType.IsArrayOrList())
                                {
                                    var memExp = callExp.Arguments[argIndex];
                                    if (memExp.NodeType == ExpressionType.MemberAccess &&
                                        memExp.Type.NullableTypeOrThis().IsEnum &&
                                        LocalParseMemberExp(memExp as MemberExpression))
                                    {
                                        if (!objExp.IsParameter())
                                        {
                                            var comonExp = (fsql.Select<object>() as Select0Provider)._commonExpression;
                                            var rightSql = comonExp.formatSql(Expression.Lambda(objExp).Compile().DynamicInvoke(), typeof(int), null, null);
                                            e.Result = $"{e.Result} in {rightSql.Replace(",   \r\n    \r\n", $") \r\n OR {e.Result} in (")}";
                                            return;
                                        }
                                        e.Result = null;
                                        return;
                                    }
                                }
                            }
                            break;
                    }

                    //解析 POCO Json   a.Customer.Name
                    if (e.Expression.NodeType == ExpressionType.MemberAccess)
                        LocalParseMemberExp(e.Expression as MemberExpression);

                    bool LocalParseMemberExp(MemberExpression memExp)
                    {
                        if (memExp == null) return false;
                        if (e.Expression.IsParameter() == false) return false;
                        var parentMemExps = new Stack<MemberExpression>();
                        parentMemExps.Push(memExp);
                        while (true)
                        {
                            switch (memExp.Expression.NodeType)
                            {
                                case ExpressionType.MemberAccess:
                                case ExpressionType.Parameter: break;
                                default: return false;
                            }
                            switch (memExp.Expression.NodeType)
                            {
                                case ExpressionType.MemberAccess:
                                    memExp = memExp.Expression as MemberExpression;
                                    if (memExp == null) return false;
                                    parentMemExps.Push(memExp);
                                    break;
                                case ExpressionType.Parameter:
                                    var tb = fsql.CodeFirst.GetTableByEntity(memExp.Expression.Type);
                                    if (tb == null) return false;
                                    if (tb.ColumnsByCs.TryGetValue(parentMemExps.Pop().Member.Name, out var trycol) == false) return false;
                                    if (_dicTypes.ContainsKey(trycol.CsType) == false) return false;
                                    var result = e.FreeParse(Expression.MakeMemberAccess(memExp.Expression, tb.Properties[trycol.CsName]));
                                    if (parentMemExps.Any() == false)
                                    {
                                        e.Result = result;
                                        return true;
                                    }
                                    var jsonPath = "";
                                    switch (fsql.Ado.DataType)
                                    {
                                        case DataType.Sqlite:
                                        case DataType.MySql:
                                        case DataType.OdbcMySql:
                                        case DataType.CustomMySql:
                                            StyleJsonExtract();
                                            return true;
                                        case DataType.SqlServer:
                                        case DataType.OdbcSqlServer:
                                        case DataType.CustomSqlServer:
                                        case DataType.Oracle:
                                        case DataType.OdbcOracle:
                                        case DataType.CustomOracle:
                                        case DataType.Dameng:
                                            StyleJsonValue();
                                            return true;
                                        case DataType.DuckDB:
                                            StyleDotAccess();
                                            return true;
                                    }
                                    StylePgJson();
                                    return true;

                                    void StyleJsonExtract()
                                    {
                                        while (parentMemExps.Any())
                                        {
                                            memExp = parentMemExps.Pop();
                                            jsonPath = $"{jsonPath}.{memExp.Member.Name}";
                                        }
                                        e.Result = $"json_extract({result},'${jsonPath}')";
                                    }
                                    void StyleJsonValue()
                                    {
                                        while (parentMemExps.Any())
                                        {
                                            memExp = parentMemExps.Pop();
                                            jsonPath = $"{jsonPath}.{memExp.Member.Name}";
                                        }
                                        e.Result = $"json_value({result},'${jsonPath}')";
                                    }
                                    void StyleDotAccess()
                                    {
                                        while (parentMemExps.Any())
                                        {
                                            memExp = parentMemExps.Pop();
                                            result = $"{result}['{memExp.Member.Name}']";
                                        }
                                        e.Result = result;
                                    }
                                    void StylePgJson()
                                    {
                                        while (parentMemExps.Any())
                                        {
                                            memExp = parentMemExps.Pop();
                                            var opt = parentMemExps.Any() ? "->" : $"->>{(memExp.Type.IsArrayOrList() ? "/*json array*/" : "")}";
                                            result = $"{result}{opt}'{memExp.Member.Name}'";
                                        }
                                        e.Result = result;
                                    }
                            }
                        }
                    }
                };
                break;
        }
    }
}