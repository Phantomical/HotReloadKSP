using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HotReloadKSP;

public static class HotReload
{
    /// <summary>
    /// Orchestrates a full reload: swaps <paramref name="newAssembly"/> into KSP's
    /// AssemblyLoader, updates type lookups, and reloads live VesselModule and
    /// PartModule instances. Must be called on the Unity main thread.
    /// </summary>
    public static void Reload(Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        var sw = Stopwatch.StartNew();
        Log.Info($"Reloading {newAssembly.GetName().Name}");

        var oldAssembly = LoadAssembly(newAssembly);
        UpdateTypeLookups(oldAssembly, newAssembly);
        ReloadVesselModules(oldAssembly, newAssembly);
        ReloadPartModules(oldAssembly, newAssembly);
        ReloadScenarioModules(oldAssembly, newAssembly);

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
}
