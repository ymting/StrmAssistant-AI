using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StrmAssistant.Options;

namespace StrmAssistant.Mod
{
    internal enum PatchApproach
    {
        Harmony,
        Reflection,
        None
    }

    internal interface IMod
    {
    }

    /// <summary>记录某个 mod 的补丁方式；Harmony 不可用时降级为 Reflection/None。</summary>
    internal class PatchTracker
    {
        public string Name { get; }
        public Type PatchType { get; }
        public PatchApproach DefaultPatchApproach { get; }
        public PatchApproach FallbackPatchApproach { get; set; }
        public bool IsSupported { get; set; } = true;

        public PatchTracker(Type patchType, PatchApproach defaultApproach)
        {
            Name = patchType.Name;
            PatchType = patchType;
            DefaultPatchApproach = defaultApproach;
            FallbackPatchApproach = defaultApproach;
            PatchManager.Register(this);
        }
    }

    internal abstract class PatchBase<T> : IMod where T : PatchBase<T>
    {
        public static T Instance { get; private set; }

        public PatchTracker PatchTracker { get; }

        protected PatchBase()
        {
            Instance = (T)this;
            PatchTracker = new PatchTracker(typeof(T), PatchApproach.Harmony);
        }

        /// <summary>初始化：拿补丁目标方法，失败则降级，不影响其它功能。</summary>
        public void Initialize()
        {
            try
            {
                OnInitialize();
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                Plugin.Instance.Logger.Warn($"{PatchTracker.Name} Init Failed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (PatchTracker.FallbackPatchApproach != PatchApproach.None && PatchManager.HarmonyMod is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
            }
        }

        protected virtual void OnInitialize()
        {
        }

        public void Patch()
        {
            Prepare(true);
        }

        public void Unpatch()
        {
            Prepare(false);
        }

        protected abstract void Prepare(bool apply);
    }

    /// <summary>
    /// 极简版 Harmony 补丁管理，只保留本分支实际用到的能力。
    /// 完整社区版这里挂了 25 个 mod，本分支只移植「代理服务器」与「中文搜索增强」。
    /// </summary>
    internal static class PatchManager
    {
        internal static readonly Harmony HarmonyMod;
        private static readonly List<PatchTracker> Trackers = new List<PatchTracker>();
        private static readonly ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod> HarmonyMethodCache =
            new ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod>();

        static PatchManager()
        {
            try
            {
                HarmonyMod = new Harmony("StrmAssistantLite.mod");
            }
            catch (Exception e)
            {
                // Harmony 初始化失败不致命，调用方会降级为 Reflection/None
                Plugin.Instance?.Logger?.Warn($"Harmony Init Failed: {e.Message}");
            }
        }

        internal static void Register(PatchTracker tracker)
        {
            lock (Trackers)
            {
                Trackers.Add(tracker);
            }
        }

        internal static void Initialize()
        {
            // 构造实例即完成 PatchTracker 注册；Apply() 按当前选项决定 patch 还是 unpatch
            EnableProxyServer.Instance?.Apply();
        }

        internal static void InitializeMods()
        {
            _ = EnableProxyServer.Instance;
        }

        internal static bool IsModSuccess()
        {
            List<PatchTracker> snapshot;
            lock (Trackers) snapshot = Trackers.ToList();

            return snapshot.Where(t => t.IsSupported)
                .All(t => t.FallbackPatchApproach == t.DefaultPatchApproach);
        }

        internal static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod,
            string prefix = null, string postfix = null, string transpiler = null, string finalizer = null)
        {
            if (tracker.FallbackPatchApproach != PatchApproach.Harmony) return false;
            if (HarmonyMod is null) return false;

            if (targetMethod is null)
            {
                Plugin.Instance.Logger.Warn($"{tracker.Name} Init Failed - target method not found");
                tracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }

            try
            {
                if (apply && !IsPatched(targetMethod, tracker.PatchType))
                {
                    HarmonyMod.Patch(targetMethod,
                        GetHarmonyMethod(tracker.PatchType, prefix),
                        GetHarmonyMethod(tracker.PatchType, postfix),
                        GetHarmonyMethod(tracker.PatchType, transpiler),
                        GetHarmonyMethod(tracker.PatchType, finalizer));
                }
                else if (!apply && IsPatched(targetMethod, tracker.PatchType))
                {
                    foreach (var name in new[] { prefix, postfix, transpiler, finalizer })
                    {
                        var method = GetMethodInfo(tracker.PatchType, name);
                        if (method != null) HarmonyMod.Unpatch(targetMethod, method);
                    }
                }

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"{(apply ? "Patch" : "Unpatch")} {targetMethod.DeclaringType?.Name}.{targetMethod.Name} " +
                        $"for {tracker.Name} Success");
                }

                return true;
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                tracker.FallbackPatchApproach = PatchApproach.Reflection;
                Plugin.Instance.Logger.Warn($"{tracker.Name} {(apply ? "Patch" : "Unpatch")} Failed");
            }

            return false;
        }

        private static bool IsPatched(MethodBase method, Type patchType)
        {
            if (!Harmony.GetAllPatchedMethods().Contains(method)) return false;

            var info = Harmony.GetPatchInfo(method);
            if (info is null) return false;

            return info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == patchType);
        }

        private static HarmonyMethod GetHarmonyMethod(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return HarmonyMethodCache.GetOrAdd(Tuple.Create(patchType, patchMethod), key =>
            {
                var method = GetMethodInfo(key.Item1, key.Item2);
                return method is null ? null : new HarmonyMethod(method);
            });
        }

        private static MethodInfo GetMethodInfo(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;
            return AccessTools.Method(patchType, patchMethod);
        }
    }
}
