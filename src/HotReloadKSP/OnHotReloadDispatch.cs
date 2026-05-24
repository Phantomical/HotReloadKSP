using System;
using System.Collections.Generic;
using System.Reflection;

namespace HotReloadKSP;

// Broadcasts `static void OnHotReload(Assembly oldAssembly, Assembly newAssembly)`
// to every type in every KSP-tracked assembly except the one being reloaded.
//
// Scanning every type in every assembly on each reload is too expensive (KSP
// has hundreds of plugins), so the discovered hook list is cached per
// Assembly. The cache is populated lazily on first use and the swapped-out
// entry is dropped after each reload so it doesn't leak.
internal static class OnHotReloadDispatch
{
    const string HookName = "OnHotReload";

    static readonly Type[] HookSignature = [typeof(Assembly), typeof(Assembly)];

    static readonly Dictionary<Assembly, MethodInfo[]> Cache = [];

    /// <summary>
    /// Invoke <c>static void OnHotReload(Assembly oldAssembly, Assembly newAssembly)</c>
    /// on every type in every <see cref="AssemblyLoader.loadedAssemblies"/> entry except
    /// the assembly being reloaded. Using KSP's registry (instead of
    /// <c>AppDomain.GetAssemblies()</c>) means previously hot-reloaded "zombie" images
    /// are excluded automatically — <see cref="AssemblySwap"/> only ever points each
    /// <c>LoadedAssembly</c> at its current image.
    /// </summary>
    public static void Broadcast(Assembly oldAssembly, Assembly newAssembly)
    {
        var loaded = AssemblyLoader.loadedAssemblies;
        for (int i = 0; i < loaded.Count; i++)
        {
            var asm = loaded[i]?.assembly;
            if (asm == null)
                continue;
            if (ReferenceEquals(asm, oldAssembly))
                continue;
            if (ReferenceEquals(asm, newAssembly))
                continue;

            var hooks = GetHooks(asm);
            for (int j = 0; j < hooks.Length; j++)
                Invoke(hooks[j], oldAssembly, newAssembly);
        }
    }

    /// <summary>
    /// Drop cache entries for the swapped pair so the next broadcast doesn't reuse
    /// stale results — <paramref name="oldAssembly"/> is no longer in KSP's loaded
    /// list, and <paramref name="newAssembly"/>'s entry (if any) was built from the
    /// pre-swap image.
    /// </summary>
    public static void OnReloaded(Assembly oldAssembly, Assembly newAssembly)
    {
        if (oldAssembly != null)
            Cache.Remove(oldAssembly);
        if (newAssembly != null)
            Cache.Remove(newAssembly);
    }

    static MethodInfo[] GetHooks(Assembly asm)
    {
        if (Cache.TryGetValue(asm, out var cached))
            return cached;

        var hooks = Scan(asm);
        Cache[asm] = hooks;
        return hooks;
    }

    static MethodInfo[] Scan(Assembly asm)
    {
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch (Exception ex)
        {
            Log.Warn($"GetTypes threw while scanning {asm.FullName} for {HookName}");
            Log.LogException(ex);
            return [];
        }

        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        List<MethodInfo> found = null;
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null)
                continue;

            MethodInfo m;
            try
            {
                m = t.GetMethod(HookName, flags, null, HookSignature, null);
            }
            catch (Exception ex)
            {
                Log.Warn($"GetMethod({HookName}) threw for {t.FullName}");
                Log.LogException(ex);
                continue;
            }
            if (m == null)
                continue;

            found ??= [];
            found.Add(m);
        }

        return found?.ToArray() ?? [];
    }

    static void Invoke(MethodInfo hook, Assembly oldAssembly, Assembly newAssembly)
    {
        try
        {
            hook.Invoke(null, [oldAssembly, newAssembly]);
        }
        catch (TargetInvocationException tie)
        {
            Log.Error($"{HookName} threw for {hook.DeclaringType?.FullName}");
            Log.LogException(tie.InnerException ?? tie);
        }
        catch (Exception ex)
        {
            Log.Error($"{HookName} threw for {hook.DeclaringType?.FullName}");
            Log.LogException(ex);
        }
    }
}
