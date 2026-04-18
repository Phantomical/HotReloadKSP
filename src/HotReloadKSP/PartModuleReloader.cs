using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HotReloadKSP;

internal static class PartModuleReloader
{
    internal struct ModuleSnapshot
    {
        public uint PartPersistentId;
        public int ModuleIndex;
        public string ModuleName;
        public ConfigNode PrefabNode;
        public ConfigNode PersistentNode;
    }

    internal struct PawSnapshot
    {
        public uint PartPersistentId;
        public bool Pinned;
    }

    internal struct ReloadSnapshot
    {
        public List<ModuleSnapshot> Modules;
        public List<PawSnapshot> Paws;
    }

    public static ReloadSnapshot SnapshotAndDetach(Assembly oldAsm)
    {
        var result = new ReloadSnapshot
        {
            Modules = new List<ModuleSnapshot>(),
            Paws = new List<PawSnapshot>(),
        };

        if (FlightGlobals.fetch == null || FlightGlobals.Vessels == null)
            return result;

        for (int vi = 0; vi < FlightGlobals.Vessels.Count; vi++)
        {
            var v = FlightGlobals.Vessels[vi];
            if (v == null || v.parts == null)
                continue;

            for (int pi = 0; pi < v.parts.Count; pi++)
            {
                var part = v.parts[pi];
                if (part == null || part.gameObject == null)
                    continue;

                bool pawClosed = false;

                for (int mi = part.Modules.Count - 1; mi >= 0; mi--)
                {
                    var m = part.Modules[mi];
                    if (m == null)
                        continue;
                    if (m.GetType().Assembly != oldAsm)
                        continue;

                    if (!pawClosed)
                    {
                        ClosePawsForPart(part, result.Paws);
                        pawClosed = true;
                    }

                    var prefabNode = FindPrefabModuleNode(part, m, mi);
                    var persistentNode = new ConfigNode(m.moduleName);
                    try
                    {
                        m.Save(persistentNode);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(
                            $"Save threw for {m.GetType().FullName} on part {part.partInfo?.name}"
                        );
                        Log.LogException(ex);
                    }

                    result.Modules.Add(
                        new ModuleSnapshot
                        {
                            PartPersistentId = part.persistentId,
                            ModuleIndex = mi,
                            ModuleName = m.moduleName,
                            PrefabNode = prefabNode,
                            PersistentNode = persistentNode,
                        }
                    );

                    part.Modules.Remove(m);
                    UnityEngine.Object.DestroyImmediate(m);
                }

                var stray = part.gameObject.GetComponents<PartModule>();
                for (int k = 0; k < stray.Length; k++)
                {
                    var c = stray[k];
                    if (c == null)
                        continue;
                    if (c.GetType().Assembly != oldAsm)
                        continue;
                    UnityEngine.Object.DestroyImmediate(c);
                }
            }
        }

        return result;
    }

    public static void ReloadPrefabs(Assembly oldAsm, Assembly newAsm)
    {
        if (PartLoader.Instance == null || PartLoader.Instance.loadedParts == null)
            return;

        var loaded = PartLoader.Instance.loadedParts;
        for (int i = 0; i < loaded.Count; i++)
        {
            var ap = loaded[i];
            if (ap == null || ap.partPrefab == null || ap.partConfig == null)
                continue;

            var prefab = ap.partPrefab;
            var matches = new List<(int Index, string Name)>();
            for (int mi = 0; mi < prefab.Modules.Count; mi++)
            {
                var m = prefab.Modules[mi];
                if (m == null)
                    continue;
                if (m.GetType().Assembly != oldAsm)
                    continue;
                matches.Add((mi, m.moduleName));
            }

            if (matches.Count == 0)
                continue;

            bool touched = false;
            for (int k = matches.Count - 1; k >= 0; k--)
            {
                var (origIndex, name) = matches[k];
                if (origIndex >= prefab.Modules.Count)
                    continue;

                var old = prefab.Modules[origIndex];
                if (old == null || old.moduleName != name)
                    continue;

                var node = FindPrefabModuleNode(prefab, old, origIndex);
                if (node == null)
                {
                    Log.Warn(
                        $"Prefab {ap.name} module {name} at index {origIndex} has no matching MODULE node in partConfig; skipping"
                    );
                    continue;
                }

                prefab.Modules.Remove(old);
                UnityEngine.Object.DestroyImmediate(old);

                PartModule added;
                try
                {
                    added = prefab.AddModule(node, forceAwake: true);
                }
                catch (Exception ex)
                {
                    Log.Error($"AddModule threw during prefab rebuild for {ap.name}/{name}");
                    Log.LogException(ex);
                    continue;
                }

                if (added == null)
                    continue;

                MoveToIndex(prefab.Modules, added, origIndex);
                touched = true;
            }

            if (touched)
                prefab.ClearModuleReferenceCache();
        }
    }

    public static void ReattachAndRestore(ReloadSnapshot state, Assembly newAsm)
    {
        if (FlightGlobals.fetch == null)
            return;
        if (state.Modules.Count == 0)
        {
            ReopenPaws(state.Paws);
            return;
        }

        var byPart = new Dictionary<uint, List<ModuleSnapshot>>();
        foreach (var s in state.Modules)
        {
            if (!byPart.TryGetValue(s.PartPersistentId, out var list))
            {
                list = new List<ModuleSnapshot>();
                byPart[s.PartPersistentId] = list;
            }
            list.Add(s);
        }

        foreach (var kv in byPart)
        {
            var part = FindPartByPersistentId(kv.Key);
            if (part == null)
            {
                Log.Warn($"Part with persistentId {kv.Key} not found at reattach time; skipping");
                continue;
            }

            var partSnaps = kv.Value;
            partSnaps.Sort((a, b) => a.ModuleIndex.CompareTo(b.ModuleIndex));

            foreach (var snap in partSnaps)
            {
                if (snap.PrefabNode == null)
                {
                    Log.Warn(
                        $"No prefab MODULE node captured for {snap.ModuleName} on part {part.partInfo?.name}; skipping"
                    );
                    continue;
                }

                PartModule added;
                try
                {
                    added = part.AddModule(snap.PrefabNode, forceAwake: true);
                }
                catch (Exception ex)
                {
                    Log.Error(
                        $"AddModule threw for {snap.ModuleName} on part {part.partInfo?.name}"
                    );
                    Log.LogException(ex);
                    continue;
                }

                if (added == null)
                    continue;

                try
                {
                    added.Load(snap.PersistentNode);
                }
                catch (Exception ex)
                {
                    Log.Error($"Load threw for {snap.ModuleName} on part {part.partInfo?.name}");
                    Log.LogException(ex);
                }

                int target = Mathf.Clamp(snap.ModuleIndex, 0, part.Modules.Count - 1);
                MoveToIndex(part.Modules, added, target);
            }

            part.ClearModuleReferenceCache();
        }

        ReopenPaws(state.Paws);
    }

    static ConfigNode FindPrefabModuleNode(Part part, PartModule m, int moduleIndex)
    {
        var partConfig = part.partInfo?.partConfig;
        if (partConfig == null)
            return null;

        var moduleNodes = partConfig.GetNodes("MODULE");
        if (moduleNodes == null || moduleNodes.Length == 0)
            return null;

        int ord = 0;
        for (int i = 0; i < moduleIndex && i < part.Modules.Count; i++)
        {
            var pm = part.Modules[i];
            if (pm != null && pm.moduleName == m.moduleName)
                ord++;
        }

        int seen = 0;
        for (int i = 0; i < moduleNodes.Length; i++)
        {
            var n = moduleNodes[i];
            if (n.GetValue("name") != m.moduleName)
                continue;
            if (seen == ord)
                return n;
            seen++;
        }

        return null;
    }

    static void MoveToIndex(PartModuleList list, PartModule module, int index)
    {
        var inner = list.modules;
        int current = inner.IndexOf(module);
        if (current < 0 || current == index)
            return;
        if (index < 0)
            index = 0;
        if (index >= inner.Count)
            index = inner.Count - 1;
        inner.RemoveAt(current);
        inner.Insert(index, module);
    }

    static Part FindPartByPersistentId(uint persistentId)
    {
        var vessels = FlightGlobals.Vessels;
        if (vessels == null)
            return null;
        for (int vi = 0; vi < vessels.Count; vi++)
        {
            var v = vessels[vi];
            if (v == null || v.parts == null)
                continue;
            for (int pi = 0; pi < v.parts.Count; pi++)
            {
                var p = v.parts[pi];
                if (p != null && p.persistentId == persistentId)
                    return p;
            }
        }
        return null;
    }

    static void ClosePawsForPart(Part part, List<PawSnapshot> paws)
    {
        var ctrl = UIPartActionController.Instance;
        if (ctrl == null)
            return;

        bool anyFound = false;
        bool anyPinned = false;

        if (ctrl.windows != null)
        {
            for (int i = ctrl.windows.Count - 1; i >= 0; i--)
            {
                var w = ctrl.windows[i];
                if (w == null)
                    continue;
                if (w.part != part)
                    continue;
                anyFound = true;
                if (w.Pinned)
                    anyPinned = true;
                ctrl.windows.RemoveAt(i);
                UnityEngine.Object.DestroyImmediate(w.gameObject);
            }
        }

        // Also purge any hidden windows for this part; otherwise a subsequent
        // SpawnPartActionWindow would revive the hidden window with stale
        // listItems pointing at destroyed old-assembly modules.
        if (ctrl.hiddenWindows != null)
        {
            for (int i = ctrl.hiddenWindows.Count - 1; i >= 0; i--)
            {
                var w = ctrl.hiddenWindows[i];
                if (w == null)
                    continue;
                if (w.part != part)
                    continue;
                if (w.Pinned)
                    anyPinned = true;
                ctrl.hiddenWindows.RemoveAt(i);
                UnityEngine.Object.DestroyImmediate(w.gameObject);
            }
        }

        if (anyFound)
            paws.Add(new PawSnapshot { PartPersistentId = part.persistentId, Pinned = anyPinned });
    }

    static void ReopenPaws(List<PawSnapshot> paws)
    {
        if (paws == null || paws.Count == 0)
            return;
        var ctrl = UIPartActionController.Instance;
        if (ctrl == null)
            return;

        foreach (var ps in paws)
        {
            var part = FindPartByPersistentId(ps.PartPersistentId);
            if (part == null)
                continue;

            try
            {
                ctrl.SpawnPartActionWindow(part);
            }
            catch (Exception ex)
            {
                Log.Warn($"SpawnPartActionWindow threw for part {part.partInfo?.name}");
                Log.LogException(ex);
                continue;
            }

            if (!ps.Pinned)
                continue;

            var w = ctrl.GetItem(part, false);
            if (w != null && w.togglePinned != null)
                w.togglePinned.isOn = true;
        }
    }
}
