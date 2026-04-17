using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HotReloadKSP;

public readonly struct ReloadResult(
    int vesselsTouched,
    int vesselModulesDestroyed,
    int vesselModulesReattached,
    int vesselModulesRestored,
    int partsTouched,
    int partModulesDestroyed,
    int partModulesReattached,
    int partModulesRestored,
    int partPrefabsTouched,
    TimeSpan elapsed
)
{
    public int VesselsTouched { get; } = vesselsTouched;
    public int VesselModulesDestroyed { get; } = vesselModulesDestroyed;
    public int VesselModulesReattached { get; } = vesselModulesReattached;
    public int VesselModulesStateRestored { get; } = vesselModulesRestored;
    public int PartsTouched { get; } = partsTouched;
    public int PartModulesDestroyed { get; } = partModulesDestroyed;
    public int PartModulesReattached { get; } = partModulesReattached;
    public int PartModulesStateRestored { get; } = partModulesRestored;
    public int PartPrefabsTouched { get; } = partPrefabsTouched;
    public TimeSpan Elapsed { get; } = elapsed;

    public override string ToString() =>
        $"vessels {VesselsTouched} (vm -{VesselModulesDestroyed}/+{VesselModulesReattached}/r{VesselModulesStateRestored}), "
        + $"parts {PartsTouched} prefabs {PartPrefabsTouched} (pm -{PartModulesDestroyed}/+{PartModulesReattached}/r{PartModulesStateRestored}), "
        + $"{Elapsed.TotalMilliseconds:F1} ms";
}

public static class HotReload
{
    /// <summary>
    /// Orchestrates a full reload: swaps <paramref name="newAssembly"/> into KSP's
    /// AssemblyLoader, updates type lookups, and reloads live VesselModule and
    /// PartModule instances. Must be called on the Unity main thread.
    /// </summary>
    public static ReloadResult Reload(Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        var sw = Stopwatch.StartNew();
        Log.Info("Reloading " + newAssembly.GetName().Name);

        var oldAssembly = LoadAssembly(newAssembly);
        UpdateTypeLookups(oldAssembly, newAssembly);

        var vm = ReloadVesselModules(oldAssembly, newAssembly);
        var pm = ReloadPartModules(oldAssembly, newAssembly);

        sw.Stop();
        var result = new ReloadResult(
            vm.VesselsTouched,
            vm.Destroyed,
            vm.Reattached,
            vm.Restored,
            pm.PartsTouched,
            pm.Destroyed,
            pm.Reattached,
            pm.Restored,
            pm.PrefabsTouched,
            sw.Elapsed
        );
        Log.Info("Reload complete: " + result);
        return result;
    }

    struct VesselModuleCounts
    {
        public int VesselsTouched;
        public int Destroyed;
        public int Reattached;
        public int Restored;
    }

    struct PartModuleCounts
    {
        public int PartsTouched;
        public int Destroyed;
        public int Reattached;
        public int Restored;
        public int PrefabsTouched;
    }

    /// <summary>
    /// Install <paramref name="newAssembly"/> into <see cref="AssemblyLoader.loadedAssemblies"/>.
    /// If an entry with the same simple name already exists, the entry is mutated in place
    /// (keeping object identity) and the previously-loaded <see cref="Assembly"/> is returned.
    /// Returns <c>null</c> on first load.
    /// </summary>
    static Assembly LoadAssembly(Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        return AssemblySwap.Swap(newAssembly).OldAssembly;
    }

    /// <summary>
    /// Clear KSP static caches that would otherwise keep old-assembly <see cref="Type"/> tokens
    /// live, and replace the <see cref="VesselModuleManager"/> registry entries for the reloaded
    /// assembly with wrappers built from <paramref name="newAssembly"/>.
    /// </summary>
    static void UpdateTypeLookups(Assembly oldAssembly, Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

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
    static VesselModuleCounts ReloadVesselModules(Assembly oldAssembly, Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        List<VesselModuleReloader.ModuleSnapshot> snapshots;
        int vesselsTouched = 0;
        int destroyed = 0;

        if (oldAssembly == null)
        {
            snapshots = new List<VesselModuleReloader.ModuleSnapshot>();
        }
        else
        {
            snapshots = VesselModuleReloader.SnapshotAndDetach(
                oldAssembly,
                out vesselsTouched,
                out destroyed
            );
        }

        var attachCounts = VesselModuleReloader.ReattachAndRestore(snapshots, newAssembly);

        return new VesselModuleCounts
        {
            VesselsTouched = vesselsTouched,
            Destroyed = destroyed,
            Reattached = attachCounts.Attached,
            Restored = attachCounts.Restored,
        };
    }

    /// <summary>
    /// Snapshot every live <see cref="PartModule"/> whose type comes from
    /// <paramref name="oldAssembly"/>, rebuild the matching components on part prefabs from
    /// each <see cref="AvailablePart.partConfig"/>, then re-add fresh components on live parts
    /// (prefab-config init followed by a persistent-state overlay). First-time loads skip
    /// the live and prefab sweeps since there is no old assembly to match against.
    /// </summary>
    static PartModuleCounts ReloadPartModules(Assembly oldAssembly, Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        if (oldAssembly == null)
            return default;

        var snapshots = PartModuleReloader.SnapshotAndDetach(
            oldAssembly,
            out int partsTouched,
            out int destroyed
        );

        int prefabsTouched = PartModuleReloader.ReloadPrefabs(oldAssembly, newAssembly);

        var attachCounts = PartModuleReloader.ReattachAndRestore(snapshots, newAssembly);

        return new PartModuleCounts
        {
            PartsTouched = partsTouched,
            Destroyed = destroyed,
            Reattached = attachCounts.Reattached,
            Restored = attachCounts.Restored,
            PrefabsTouched = prefabsTouched,
        };
    }
}
