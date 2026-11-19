using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby 内部（Emby.Providers / Emby.Server.Implementations）的类型和方法不属于公开契约，
    /// 各版本之间会增删依赖参数、调整参数顺序。这里统一提供「按类型挑构造函数」「按参数名绑定实参」
    /// 两个能力，让插件在 4.8 / 4.9 / 4.10 等版本上都能把调用凑出来，而不是硬编码某一份签名。
    /// </summary>
    internal static class ReflectionHelper
    {
        /// <summary>
        /// 在候选服务中为每个构造函数参数找匹配实例，挑选「参数最多且全部可满足」的公开构造函数。
        /// 这样 Emby 新增依赖时，只要我们能提供对应服务就仍然可用。
        /// </summary>
        public static ConstructorInfo ResolveConstructor(Type type, params object[] candidates)
        {
            if (type is null) return null;

            var usable = candidates?.Where(c => c != null).ToArray() ?? Array.Empty<object>();

            return type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(ctor => ctor.GetParameters()
                    .All(p => usable.Any(c => p.ParameterType.IsInstanceOfType(c))))
                .OrderByDescending(ctor => ctor.GetParameters().Length)
                .FirstOrDefault();
        }

        /// <summary>
        /// 按参数名绑定实参；名称未命中时按类型在候选中兜底，仍无法确定则填默认值（引用类型为 null）。
        /// </summary>
        public static object[] BindArguments(MethodBase method, IDictionary<string, object> namedValues,
            params object[] typeCandidates)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            var usable = typeCandidates?.Where(c => c != null).ToArray() ?? Array.Empty<object>();

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                if (namedValues != null && parameter.Name != null &&
                    namedValues.TryGetValue(parameter.Name, out var namedValue))
                {
                    args[i] = namedValue;
                    continue;
                }

                // CancellationToken 一律给 None，避免漏传导致反射调用直接抛异常
                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    args[i] = CancellationToken.None;
                    continue;
                }

                var matched = usable.FirstOrDefault(c => parameter.ParameterType.IsInstanceOfType(c));

                if (matched != null)
                {
                    args[i] = matched;
                }
                else
                {
                    args[i] = parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
                }
            }

            return args;
        }

        /// <summary>
        /// 在类型中按名称挑选重载，优先取参数最多的一个（Emby 通常是在末尾追加参数）。
        /// </summary>
        public static MethodInfo ResolveMethod(Type type, string name, params Type[] requiredParameterTypes)
        {
            if (type is null) return null;

            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name)
                .Where(m => requiredParameterTypes is null ||
                            requiredParameterTypes.All(t =>
                                m.GetParameters().Any(p => p.ParameterType == t)))
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        /// <summary>
        /// 把 Task&lt;T&gt; 统一按 Task 等待后取出结果，避免依赖具体的泛型实参（跨版本类型可能不同）。
        /// </summary>
        public static async System.Threading.Tasks.Task<object> AwaitTaskResult(object taskObject)
        {
            if (!(taskObject is System.Threading.Tasks.Task task)) return null;

            await task.ConfigureAwait(false);

            var resultProperty = task.GetType().GetProperty("Result",
                BindingFlags.Public | BindingFlags.Instance);

            return resultProperty?.GetValue(task);
        }
    }
}
