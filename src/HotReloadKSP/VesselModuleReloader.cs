using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HotReloadKSP;

internal static class VesselModuleReloader
{
    internal struct ModuleSnapshot
    {
        public uint VesselPersistentId;
        public string TypeName;
        public ConfigNode Node;
    }

    public static List<ModuleSnapshot> SnapshotAndDetach(Assembly oldAsm)
    {
        var snapshots = new List<ModuleSnapshot>();

        if (FlightGlobals.fetch == null || FlightGlobals.Vessels == null)
            return snapshots;

        for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
        {
            var v = FlightGlobals.Vessels[i];
            if (v == null || v.gameObject == null)
                continue;

            for (int j = v.vesselModules.Count - 1; j >= 0; j--)
            {
                var m = v.vesselModules[j];
                if (m == null)
                    continue;
                if (m.GetType().Assembly != oldAsm)
                    continue;

                var node = new ConfigNode(m.GetType().Name);
                try
                {
                    m.Save(node);
                }
                catch (Exception ex)
                {
                    Log.Warn(
                        $"Save threw for {m.GetType().FullName} on vessel {v.vesselName}"
                    );
                    Log.LogException(ex);
                }

                snapshots.Add(
                    new ModuleSnapshot
                    {
                        VesselPersistentId = v.persistentId,
                        TypeName = m.GetType().Name,
                        Node = node,
                    }
                );

                v.vesselModules.RemoveAt(j);
                UnityEngine.Object.DestroyImmediate(m);
            }

            var strayComponents = v.gameObject.GetComponents<VesselModule>();
            for (int k = 0; k < strayComponents.Length; k++)
            {
                var c = strayComponents[k];
                if (c == null)
                    continue;
                if (c.GetType().Assembly != oldAsm)
                    continue;
                UnityEngine.Object.DestroyImmediate(c);
            }
        }

        return snapshots;
    }

    public static void PatchWrapperRegistry(Assembly oldAsm, Assembly newAsm)
    {
        if (oldAsm != null)
        {
            VesselModuleManager.modules.RemoveAll(w => w.type != null && w.type.Assembly == oldAsm);
        }

        Type[] types;
        try
        {
            types = newAsm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null)
                continue;
            if (t == typeof(VesselModule))
                continue;
            if (!typeof(VesselModule).IsAssignableFrom(t))
                continue;
            if (t.IsAbstract)
                continue;

            var wrapper = new VesselModuleManager.VesselModuleWrapper(t);
            try
            {
                var go = new GameObject("HotReloadVesselModuleProbe");
                try
                {
                    var vm = go.AddComponent(t) as VesselModule;
                    if (vm != null)
                    {
                        wrapper.order = vm.GetOrder();
                        UnityEngine.Object.DestroyImmediate(vm);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }

                VesselModuleManager.modules.Add(wrapper);
                Log.Info($"Registered VesselModule {t.FullName} (order {wrapper.order})");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to register VesselModule {t.FullName}");
                Log.LogException(ex);
            }
        }
    }

    public static void ReattachAndRestore(List<ModuleSnapshot> snapshots, Assembly newAsm)
    {
        if (FlightGlobals.fetch == null || FlightGlobals.Vessels == null)
            return;

        var newWrappers = new List<VesselModuleManager.VesselModuleWrapper>();
        for (int i = 0; i < VesselModuleManager.modules.Count; i++)
        {
            var w = VesselModuleManager.modules[i];
            if (!w.active)
                continue;
            if (w.type == null)
                continue;
            if (w.type.Assembly != newAsm)
                continue;
            newWrappers.Add(w);
        }
        newWrappers.Sort((a, b) => a.order.CompareTo(b.order));

        var byVessel = new Dictionary<uint, List<ModuleSnapshot>>();
        foreach (var s in snapshots)
        {
            if (!byVessel.TryGetValue(s.VesselPersistentId, out var list))
            {
                list = new List<ModuleSnapshot>();
                byVessel[s.VesselPersistentId] = list;
            }
            list.Add(s);
        }

        for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
        {
            var v = FlightGlobals.Vessels[i];
            if (v == null || v.gameObject == null)
                continue;

            foreach (var w in newWrappers)
            {
                if (v.gameObject.GetComponent(w.type) != null)
                    continue;
                var m = v.gameObject.AddComponent(w.type) as VesselModule;
                if (m == null)
                    continue;
                m.Vessel = v;
                m.enabled = m.ShouldBeActive();
                v.vesselModules.Add(m);
            }

            if (!byVessel.TryGetValue(v.persistentId, out var vesselSnaps))
                continue;

            foreach (var snap in vesselSnaps)
            {
                VesselModule target = null;
                for (int j = 0; j < v.vesselModules.Count; j++)
                {
                    var m = v.vesselModules[j];
                    if (m != null && m.GetType().Name == snap.TypeName)
                    {
                        target = m;
                        break;
                    }
                }

                if (target == null)
                {
                    Log.Warn(
                        $"Could not find reattached module {snap.TypeName} on vessel {v.vesselName}"
                    );
                    continue;
                }

                try
                {
                    target.Load(snap.Node);
                }
                catch (Exception ex)
                {
                    Log.Error(
                        $"Load threw for {snap.TypeName} on vessel {v.vesselName}"
                    );
                    Log.LogException(ex);
                }
            }
        }
    }
}
