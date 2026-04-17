using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace HotReloadKSP;

public static class HotReload
{
    /// <summary>
    /// Load the assembly at <paramref name="path"/> from disk bytes and reload it.
    /// Reads the file into memory and hands it to <see cref="Assembly.Load(byte[])"/>
    /// so repeated calls against the same path bypass the <see cref="Assembly.LoadFrom(string)"/>
    /// identity cache — without this, the second call would return the originally-loaded
    /// Assembly instance and the reload would be a destructive no-op (caches cleared and
    /// live components destroyed, then rebuilt against the same old types).
    /// A sibling <c>.pdb</c> is loaded alongside when present so debuggers keep working.
    /// </summary>
    public static void Reload(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        var bytes = File.ReadAllBytes(path);
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        var asm = File.Exists(pdbPath)
            ? Assembly.Load(bytes, File.ReadAllBytes(pdbPath))
            : Assembly.Load(bytes);

        Reload(asm);
    }

    /// <summary>
    /// Orchestrates a full reload: swaps <paramref name="newAssembly"/> into KSP's
    /// AssemblyLoader, updates type lookups, and reloads live VesselModule and
    /// PartModule instances. Must be called on the Unity main thread.
    /// </summary>
    public static void Reload(Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        if (IsAlreadyLoaded(newAssembly))
        {
            Log.Info(
                $"Skipping reload of {newAssembly.GetName().Name}: identical Assembly instance already loaded"
            );
            return;
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"Reloading {newAssembly.GetName().Name}");

        var oldAssembly = LoadAssembly(newAssembly);
        UpdateTypeLookups(oldAssembly, newAssembly);
        ReloadVesselModules(oldAssembly, newAssembly);
        ReloadPartModules(oldAssembly, newAssembly);
        ReloadScenarioModules(oldAssembly, newAssembly);
        ReloadMonoBehaviours(oldAssembly, newAssembly);
        InvokeStaticHotUnloadHooks(oldAssembly);
        InvokeStaticHotLoadHooks(newAssembly);

        sw.Stop();
        Log.Info($"Reload complete in {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>
    /// Install <paramref name="newAssembly"/> into <see cref="AssemblyLoader.loadedAssemblies"/>.
    /// If an entry with the same simple name already exists, the entry is mutated in place
    /// (keeping object identity) and the previously-loaded <see cref="Assembly"/> is returned.
    /// Returns <c>null</c> on first load.
    /// </summary>
    static Assembly LoadAssembly(Assembly newAssembly)
    {
        return AssemblySwap.Swap(newAssembly).OldAssembly;
    }

    static bool IsAlreadyLoaded(Assembly newAssembly)
    {
        var name = newAssembly.GetName().Name;
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null)
                continue;
            if (la.assembly.GetName().Name != name)
                continue;
            return la.assembly == newAssembly;
        }
        return false;
    }

    /// <summary>
    /// Clear KSP static caches that would otherwise keep old-assembly <see cref="Type"/> tokens
    /// live, and replace the <see cref="VesselModuleManager"/> registry entries for the reloaded
    /// assembly with wrappers built from <paramref name="newAssembly"/>.
    /// </summary>
    static void UpdateTypeLookups(Assembly oldAssembly, Assembly newAssembly)
    {
        AssemblyLoader.subclassesOfParentClass.Clear();
        BaseFieldList.reflectedAttributeCache.Clear();
        BaseEventList.reflectedAttributeCache.Clear();
        BaseActionList.reflectedAttributeCache.Clear();
        PartModule.reflectedAttributeCache.Clear();
        Part.reflectedAttributeCache.Clear();

        VesselModuleReloader.PatchWrapperRegistry(oldAssembly, newAssembly);
    }

    /// <summary>
    /// Snapshot every live <see cref="VesselModule"/> whose type comes from
    /// <paramref name="oldAssembly"/>, destroy the old components, then re-add fresh
    /// components built from <paramref name="newAssembly"/> and restore KSPField state.
    /// Pass <c>null</c> for <paramref name="oldAssembly"/> on a first-time load.
    /// </summary>
    static void ReloadVesselModules(Assembly oldAssembly, Assembly newAssembly)
    {
        var snapshots =
            oldAssembly == null ? [] : VesselModuleReloader.SnapshotAndDetach(oldAssembly);

        VesselModuleReloader.ReattachAndRestore(snapshots, newAssembly);
    }

    /// <summary>
    /// Snapshot every live <see cref="PartModule"/> whose type comes from
    /// <paramref name="oldAssembly"/>, rebuild the matching components on part prefabs from
    /// each <see cref="AvailablePart.partConfig"/>, then re-add fresh components on live parts
    /// (prefab-config init followed by a persistent-state overlay). First-time loads skip
    /// the live and prefab sweeps since there is no old assembly to match against.
    /// </summary>
    static void ReloadPartModules(Assembly oldAssembly, Assembly newAssembly)
    {
        if (oldAssembly == null)
            return;

        var snapshots = PartModuleReloader.SnapshotAndDetach(oldAssembly);
        PartModuleReloader.ReloadPrefabs(oldAssembly, newAssembly);
        PartModuleReloader.ReattachAndRestore(snapshots, newAssembly);
    }

    /// <summary>
    /// Snapshot every live <see cref="ScenarioModule"/> whose type comes from
    /// <paramref name="oldAssembly"/>, destroy the old components, then re-add fresh
    /// components built from <paramref name="newAssembly"/> and restore KSPField state.
    /// Also relinks the corresponding <see cref="ProtoScenarioModule"/> entries so scene
    /// transitions persist the new instances. Skips on first-time loads.
    /// </summary>
    static void ReloadScenarioModules(Assembly oldAssembly, Assembly newAssembly)
    {
        if (oldAssembly == null)
            return;

        var snapshots = ScenarioModuleReloader.SnapshotAndDetach(oldAssembly);
        ScenarioModuleReloader.ReattachAndRestore(snapshots, newAssembly);
    }

    /// <summary>
    /// Swap live MonoBehaviour components whose type is in <paramref name="oldAssembly"/> and
    /// whose new-assembly counterpart declares an <c>OnHotReload(MonoBehaviour prev)</c> instance
    /// method. Components of KSP-handled types (VesselModule, PartModule, ScenarioModule) are
    /// skipped here because earlier stages already dealt with them. Skipped on first-time loads.
    /// </summary>
    static void ReloadMonoBehaviours(Assembly oldAssembly, Assembly newAssembly)
    {
        if (oldAssembly == null)
            return;

        MonoBehaviourReloader.Reload(oldAssembly, newAssembly);
    }

    /// <summary>
    /// Invoke <c>static void OnHotUnload()</c> on every type in <paramref name="oldAssembly"/>
    /// that declares one. Runs before the new assembly's reload hooks so old-assembly types
    /// can tear down static state (caches, event subscriptions) before being stranded.
    /// No-op on first-time loads.
    /// </summary>
    static void InvokeStaticHotUnloadHooks(Assembly oldAssembly)
    {
        if (oldAssembly == null)
            return;
        InvokeStaticHooks(oldAssembly, "OnHotUnload");
    }

    /// <summary>
    /// Invoke <c>static void OnHotLoad()</c> on every type in <paramref name="newAssembly"/>
    /// that declares one. Runs after live component swaps and after old-assembly unload hooks
    /// so new hooks observe the fully-swapped scene with old static state torn down.
    /// Exceptions from individual hooks are logged but do not abort the sweep.
    /// </summary>
    static void InvokeStaticHotLoadHooks(Assembly newAssembly)
    {
        InvokeStaticHooks(newAssembly, "OnHotLoad");
    }

    static void InvokeStaticHooks(Assembly asm, string methodName)
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

        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null)
                continue;

            MethodInfo hook;
            try
            {
                hook = t.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            }
            catch (Exception ex)
            {
                Log.Warn($"GetMethod({methodName}) threw for {t.FullName}");
                Log.LogException(ex);
                continue;
            }

            if (hook == null)
                continue;

            try
            {
                hook.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                Log.Error($"{methodName} threw for {t.FullName}");
                Log.LogException(tie.InnerException ?? tie);
            }
            catch (Exception ex)
            {
                Log.Error($"{methodName} threw for {t.FullName}");
                Log.LogException(ex);
            }
        }
    }
}
