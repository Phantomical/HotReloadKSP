using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using UnityEngine;

namespace HotReloadKSP;

public static class HotReload
{
    /// <summary>
    /// An event that gets fired when an assembly gets hot-reloaded. Use this
    /// if you want to respond to the hot reload of <i>another</i> assembly.
    /// </summary>
    public static event Action<Assembly, Assembly> OnAssemblyHotReload;

    /// <summary>
    /// Load the assembly at <paramref name="path"/> from disk bytes and reload it.
    /// </summary>
    public static void Reload(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        var dllBytes = File.ReadAllBytes(path);

        // Unity's Mono dedupes Assembly.Load by simple name alone — if an
        // assembly with the same simple name is already loaded, the given bytes
        // are ignored and the existing Assembly instance is returned, even when
        // AssemblyVersion and MVID differ. Bumping identity fields isn't enough,
        // so rewrite the incoming image with a unique simple name so Mono must
        // treat it as a new assembly. The original simple name is passed through
        // to AssemblySwap so the existing AssemblyLoader entry (and everything
        // that looks it up by name) can still be found.
        //
        // PDB rewriting is skipped: KSP's bundled Mono.Cecil is too old to
        // include a concrete symbol provider, so reloaded assemblies don't carry
        // debug info. The originally-loaded assembly keeps its PDB; only the
        // hot-reloaded image loses line numbers in stack traces.
        var (rewritten, originalName) = RewriteIdentity(dllBytes);
        ReloadCore(Assembly.Load(rewritten), originalName);
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

        ReloadCore(newAssembly, newAssembly.GetName().Name);
    }

    static void ReloadCore(Assembly newAssembly, string targetSimpleName)
    {
        if (IsAlreadyLoaded(newAssembly, targetSimpleName))
        {
            Log.Info(
                $"Skipping reload of {targetSimpleName}: identical Assembly instance already loaded"
            );
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"Reloading {targetSimpleName}");

        var oldAssembly = AssemblySwap.Swap(newAssembly, targetSimpleName).OldAssembly;
        UpdateTypeLookups(oldAssembly, newAssembly);
        ReloadVesselModules(oldAssembly, newAssembly);
        ReloadPartModules(oldAssembly, newAssembly);
        ReloadScenarioModules(oldAssembly, newAssembly);

        // Two-phase MonoBehaviour reload: new components are attached on inactive
        // parents first so static OnHotLoad can populate new-assembly static state
        // before any reattached component's OnEnable runs, and OnHotUnload can tear
        // down old-assembly static state while its live components still exist.
        var pending =
            oldAssembly == null
                ? MonoBehaviourReloader.Pending.Empty
                : MonoBehaviourReloader.PrepareReload(oldAssembly, newAssembly);
        InvokeStaticHotLoadHooks(newAssembly);
        StopPQSSpheres(pending.PQSToRebuild);

        try
        {
            OnAssemblyHotReload?.Invoke(oldAssembly, newAssembly);
        }
        catch (Exception e)
        {
            Debug.LogError("OnAssemblyHotReload threw an exception");
            Debug.LogException(e);
        }

        InvokeStaticHotUnloadHooks(oldAssembly);
        MonoBehaviourReloader.FinalizeReload(pending);
        StartPQSSpheres(pending.PQSToRebuild);

        sw.Stop();
        Log.Info($"Reload complete in {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    static (byte[] bytes, string originalName) RewriteIdentity(byte[] dllBytes)
    {
        using var dllIn = new MemoryStream(dllBytes, writable: false);
        var asmDef = AssemblyDefinition.ReadAssembly(
            dllIn,
            new ReaderParameters { ReadSymbols = false }
        );

        var originalName = asmDef.Name.Name;
        asmDef.Name.Name = $"{originalName}-HR{Guid.NewGuid():N}";
        asmDef.MainModule.Mvid = Guid.NewGuid();

        using var dllOut = new MemoryStream();
        asmDef.Write(dllOut);
        return (dllOut.ToArray(), originalName);
    }

    static bool IsAlreadyLoaded(Assembly newAssembly, string simpleName)
    {
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null)
                continue;
            if (la.name != simpleName)
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

        var state = PartModuleReloader.SnapshotAndDetach(oldAssembly);
        PartModuleReloader.ReloadPrefabs(oldAssembly, newAssembly);
        PartModuleReloader.ReattachAndRestore(state, newAssembly);
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
    /// Invoke <c>static void OnHotUnload()</c> on every type in <paramref name="oldAssembly"/>
    /// that declares one. Runs after new components have been attached (inactive) and after the
    /// new assembly's <c>OnHotLoad</c> hooks, so old-assembly types can tear down static state
    /// once the new-assembly equivalents are ready to take over. No-op on first-time loads.
    /// </summary>
    static void InvokeStaticHotUnloadHooks(Assembly oldAssembly)
    {
        if (oldAssembly == null)
            return;
        InvokeStaticHooks(oldAssembly, "OnHotUnload");
    }

    /// <summary>
    /// Invoke <c>static void OnHotLoad()</c> on every type in <paramref name="newAssembly"/>
    /// that declares one. Runs after replacement components have been attached (while still
    /// inactive) and before their parent GameObjects are re-enabled, so new static state
    /// (prefab caches, registries) is populated before any reattached component's
    /// <c>OnEnable</c> observes it. Exceptions from individual hooks are logged but do not
    /// abort the sweep.
    /// </summary>
    static void InvokeStaticHotLoadHooks(Assembly newAssembly)
    {
        InvokeStaticHooks(newAssembly, "OnHotLoad");
    }

    static void StopPQSSpheres(IEnumerable<PQS> spheres)
    {
        foreach (var pqs in spheres)
        {
            if (pqs == null || !pqs.isActiveAndEnabled)
                continue;
            try
            {
                pqs.ResetSphere();
            }
            catch (Exception ex)
            {
                Log.Error($"PQS.RebuildSphere threw for {pqs.name}");
                Log.LogException(ex);
            }
        }
    }

    /// <summary>
    /// Rebuild every active <see cref="PQS"/> whose GameObject hosted a reloaded
    /// <see cref="PQSMod"/>. A PQS caches its mod list and built geometry, so swapped-in
    /// mods don't contribute until the sphere is rebuilt.
    /// </summary>
    static void StartPQSSpheres(HashSet<PQS> spheres)
    {
        foreach (var pqs in spheres)
        {
            if (pqs == null || !pqs.isActiveAndEnabled)
                continue;
            try
            {
                pqs.StartSphere(force: false);
            }
            catch (Exception ex)
            {
                Log.Error($"PQS.RebuildSphere threw for {pqs.name}");
                Log.LogException(ex);
            }
        }
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
