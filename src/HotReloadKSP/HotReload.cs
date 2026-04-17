using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HotReloadKSP;

public readonly struct ReloadResult(
    int vesselsTouched,
    int destroyed,
    int reattached,
    int restored,
    TimeSpan elapsed
)
{
    public int VesselsTouched { get; } = vesselsTouched;
    public int ModulesDestroyed { get; } = destroyed;
    public int ModulesReattached { get; } = reattached;
    public int ModulesStateRestored { get; } = restored;
    public TimeSpan Elapsed { get; } = elapsed;

    public override string ToString() =>
        $"{VesselsTouched} vessel(s), destroyed {ModulesDestroyed}, attached {ModulesReattached}, restored {ModulesStateRestored}, {Elapsed.TotalMilliseconds:F1} ms";
}

public static class HotReload
{
    /// <summary>
    /// Orchestrates a full reload: swaps <paramref name="newAssembly"/> into KSP's
    /// AssemblyLoader, updates type lookups, and reloads live VesselModule instances.
    /// Must be called on the Unity main thread.
    /// </summary>
    public static ReloadResult Reload(Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        var sw = Stopwatch.StartNew();
        Log.Info("Reloading " + newAssembly.GetName().Name);

        var oldAssembly = LoadAssembly(newAssembly);
        UpdateTypeLookups(oldAssembly, newAssembly);
        var counts = ReloadVesselModules(oldAssembly, newAssembly);

        sw.Stop();
        var result = new ReloadResult(
            counts.VesselsTouched,
            counts.ModulesDestroyed,
            counts.ModulesReattached,
            counts.ModulesStateRestored,
            sw.Elapsed
        );
        Log.Info("Reload complete: " + result);
        return result;
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

        VesselModuleReloader.PatchWrapperRegistry(oldAssembly, newAssembly);
    }

    /// <summary>
    /// Snapshot every live <see cref="VesselModule"/> whose type comes from
    /// <paramref name="oldAssembly"/>, destroy the old components, then re-add fresh
    /// components built from <paramref name="newAssembly"/> and restore KSPField state.
    /// Pass <c>null</c> for <paramref name="oldAssembly"/> on a first-time load.
    /// </summary>
    static ReloadResult ReloadVesselModules(Assembly oldAssembly, Assembly newAssembly)
    {
        if (newAssembly == null)
            throw new ArgumentNullException(nameof(newAssembly));

        var sw = Stopwatch.StartNew();

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

        sw.Stop();
        return new ReloadResult(
            vesselsTouched,
            destroyed,
            attachCounts.Attached,
            attachCounts.Restored,
            sw.Elapsed
        );
    }
}
