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
        public int ModuleOrd;
        public string ModuleName;
        public ConfigNode PrefabNode;
        public ConfigNode PersistentNode;

        // Old module is kept alive across the swap so we can read its fields if
        // needed and remap part-level references that pointed at it; destroyed
        // during ReattachAndRestore finalization, mirroring MonoBehaviourReloader's
        // FinalizeReload pattern.
        public PartModule OldModule;
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

        foreach (var part in EnumerateLiveParts())
        {
            if (part == null || part.gameObject == null)
                continue;

            bool pawClosed = false;
            var captured = new HashSet<PartModule>();

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

                int ord = CountSameNamedBefore(part.Modules, m.moduleName, mi);

                var prefabNode = FindPrefabModuleNode(part, m.moduleName, ord);
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
                        ModuleOrd = ord,
                        ModuleName = m.moduleName,
                        PrefabNode = prefabNode,
                        PersistentNode = persistentNode,
                        OldModule = m,
                    }
                );

                captured.Add(m);

                // Remove from the modules list so part.Modules reflects only
                // live, valid modules during reattach. The Component itself
                // stays alive on the GameObject until ReattachAndRestore
                // finalizes - we may need to read its fields and we want
                // part-level reference remap to be able to identify it.
                part.Modules.Remove(m);
            }

            // Catch any leftover oldAsm Components that weren't tracked in
            // part.Modules (orphans from earlier reloads, weird states); leave
            // captured ones alone so finalization can destroy them after the
            // remap pass runs.
            var stray = part.gameObject.GetComponents<PartModule>();
            for (int k = 0; k < stray.Length; k++)
            {
                var c = stray[k];
                if (c == null)
                    continue;
                if (c.GetType().Assembly != oldAsm)
                    continue;
                if (captured.Contains(c))
                    continue;
                UnityEngine.Object.DestroyImmediate(c);
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

                int ord = CountSameNamedBefore(prefab.Modules, name, origIndex);
                var node = FindPrefabModuleNode(prefab, name, ord);
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

                UnitySerializationNormalizer.Normalize(added);

                // Capture the post-cfg [KSPField] values as the prefab's
                // "original" baseline so future Object.Instantiate'd live
                // modules see the same revert/upgrade baseline PartLoader
                // would have produced at startup.
                try
                {
                    added.Fields?.SetOriginalValue();
                }
                catch (Exception ex)
                {
                    Log.Warn($"SetOriginalValue threw for prefab {ap.name}/{name}");
                    Log.LogException(ex);
                }

                MoveToIndex(prefab.Modules, added, origIndex);
                touched = true;
            }

            if (touched)
                prefab.ClearModuleReferenceCache();
        }
    }

    public static void ReattachAndRestore(ReloadSnapshot state, Assembly newAsm)
    {
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

        var oldModulesToDestroy = new List<PartModule>(state.Modules.Count);

        foreach (var kv in byPart)
        {
            var part = FindPartByPersistentId(kv.Key);
            if (part == null)
            {
                Log.Warn($"Part with persistentId {kv.Key} not found at reattach time; skipping");
                // Schedule the orphaned old modules for destruction anyway.
                foreach (var s in kv.Value)
                    if (s.OldModule != null)
                        oldModulesToDestroy.Add(s.OldModule);
                continue;
            }

            var partSnaps = kv.Value;
            partSnaps.Sort((a, b) => a.ModuleIndex.CompareTo(b.ModuleIndex));

            var rebuilt = new List<PartModule>(partSnaps.Count);
            var remap = new Dictionary<PartModule, PartModule>(partSnaps.Count);

            foreach (var snap in partSnaps)
            {
                // Bare AddComponent path - mirrors what Object.Instantiate
                // produces during a real scene switch: single Awake (which
                // runs ModularSetup, sets `part = GetComponent<Part>()`,
                // OnAwake, resHandler init), no extra Load(prefabNode).
                PartModule added;
                try
                {
                    added = part.AddModule(snap.ModuleName);
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

                // Mirror what Unity's serializer carries during Instantiate:
                // copy all serializable instance fields from the freshly
                // rebuilt prefab module to the new live module. The prefab
                // module was already Normalize'd by ReloadPrefabs, so this
                // pulls Unity-default state for nulls plus any cfg-driven
                // [KSPField] values the prefab inherited from Load(prefabNode).
                var prefabModule = FindPrefabModule(part, snap.ModuleName, snap.ModuleOrd);
                if (prefabModule != null)
                    CopySerializedFields(prefabModule, added);
                else
                    Log.Warn(
                        $"Prefab module {snap.ModuleName} (ord {snap.ModuleOrd}) not found on {part.partInfo?.name}; new module starts at C# defaults"
                    );

                // Snapshot cfg-default [KSPField] state as the "original"
                // baseline for tweakable revert / upgrade-stats UI before
                // overlaying persistent state, matching ProtoPartSnapshot.cs:924.
                try
                {
                    added.Fields?.SetOriginalValue();
                }
                catch (Exception ex)
                {
                    Log.Warn(
                        $"SetOriginalValue threw for {snap.ModuleName} on part {part.partInfo?.name}"
                    );
                    Log.LogException(ex);
                }

                // Persistent-state overlay - same call chain that
                // ConfigurePart triggers via LoadModule(node, ref idx) in
                // KSP's normal scene-switch pipeline.
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

                rebuilt.Add(added);
                if (snap.OldModule != null)
                    remap[snap.OldModule] = added;

                RewireSnapshot(part, snap, added);
            }

            part.ClearModuleReferenceCache();

            // Mirror the Part-level reference remap from
            // ProtoPartSnapshot.cs:925-933: any non-value-type publicField on
            // the Part that pointed at one of the destroyed-old modules gets
            // repointed at its replacement.
            RemapPartLevelReferences(part, remap);

            // Vessel.Initialize / ShipConstruct.LoadShip parity.
            RunOnInitializePass(part, rebuilt);

            // Part.Start parity (ModulesOnStart -> ModulesBeforePartAttachJoint
            // -> ModulesOnStartFinished).
            RunStartPipeline(part, rebuilt);

            foreach (var snap in partSnaps)
                if (snap.OldModule != null)
                    oldModulesToDestroy.Add(snap.OldModule);
        }

        // Destroy old modules last so the part-level remap has a chance to
        // identify them by reference; matches MonoBehaviourReloader.FinalizeReload.
        for (int i = 0; i < oldModulesToDestroy.Count; i++)
        {
            var old = oldModulesToDestroy[i];
            if (old == null)
                continue;
            try
            {
                UnityEngine.Object.DestroyImmediate(old);
            }
            catch (Exception ex)
            {
                Log.Warn($"DestroyImmediate threw for old PartModule {old.GetType().FullName}");
                Log.LogException(ex);
            }
        }

        ReopenPaws(state.Paws);
    }

    static void CopySerializedFields(PartModule src, PartModule dst)
    {
        if (src == null || dst == null)
            return;

        var srcType = src.GetType();
        var dstType = dst.GetType();
        if (srcType != dstType)
        {
            Log.Warn(
                $"CopySerializedFields type mismatch: src={srcType.FullName} dst={dstType.FullName}"
            );
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        var t = srcType;
        // Stop at MonoBehaviour for the same reason UnitySerializationNormalizer
        // does: engine-private fields above MonoBehaviour are not user state.
        while (t != null && t != typeof(MonoBehaviour) && t != typeof(object))
        {
            FieldInfo[] fields;
            try
            {
                fields = t.GetFields(flags);
            }
            catch (Exception ex)
            {
                Log.Warn($"GetFields threw for {t.FullName} during field copy");
                Log.LogException(ex);
                t = t.BaseType;
                continue;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.IsLiteral || f.IsInitOnly)
                    continue;
                if (!UnitySerializationNormalizer.IsUnitySerialized(f))
                    continue;
                if (IsPartModuleInfrastructureField(f))
                    continue;

                try
                {
                    f.SetValue(dst, f.GetValue(src));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Field copy threw for {f.DeclaringType?.FullName}.{f.Name}");
                    Log.LogException(ex);
                }
            }

            t = t.BaseType;
        }
    }

    // PartModule's [SerializeField] events/fields/actions are BaseEventList/
    // BaseFieldList/BaseActionList instances built by ModularSetup during the
    // new component's Awake, with each contained BaseEvent/BaseField/BaseAction
    // bound to the new module as its host. Copying them from the prefab clobbers
    // that fresh setup with prefab-bound containers, so PAW reads/writes and
    // KSPEvent invocations route to the prefab module instead of the live one.
    // resHandler likewise binds to a specific PartModule via SetPartModule.
    static bool IsPartModuleInfrastructureField(FieldInfo f)
    {
        if (f.DeclaringType != typeof(PartModule))
            return false;
        switch (f.Name)
        {
            case "events":
            case "fields":
            case "actions":
            case "resHandler":
                return true;
            default:
                return false;
        }
    }

    static void RemapPartLevelReferences(Part part, Dictionary<PartModule, PartModule> remap)
    {
        if (remap.Count == 0)
            return;

        var attrs = part.PartAttributes;
        if (attrs?.publicFields == null)
            return;

        for (int i = 0; i < attrs.publicFields.Length; i++)
        {
            var f = attrs.publicFields[i];
            if (f == null)
                continue;
            if (f.FieldType.IsValueType)
                continue;
            if (f.IsLiteral || f.IsInitOnly)
                continue;

            object current;
            try
            {
                current = f.GetValue(part);
            }
            catch (Exception ex)
            {
                Log.Warn($"GetValue threw for Part.{f.Name} during remap");
                Log.LogException(ex);
                continue;
            }

            if (current is not PartModule oldRef)
                continue;
            if (!remap.TryGetValue(oldRef, out var newRef))
                continue;

            try
            {
                f.SetValue(part, newRef);
            }
            catch (Exception ex)
            {
                Log.Warn($"SetValue threw for Part.{f.Name} during remap");
                Log.LogException(ex);
            }
        }
    }

    static void RewireSnapshot(Part part, ModuleSnapshot snap, PartModule newModule)
    {
        // Editor parts have no protoVessel; flight parts do. The proto
        // back-pointers (proto.moduleRef <-> module.snapshot) are wired by
        // ProtoPartModuleSnapshot.Load at scene-switch time; mirror that
        // here so any KSP code reaching into protoVessel sees the live new
        // module instead of a destroyed old reference.
        var proto = part.protoPartSnapshot;
        if (proto == null || proto.modules == null)
            return;

        int seen = 0;
        for (int i = 0; i < proto.modules.Count; i++)
        {
            var pm = proto.modules[i];
            if (pm == null || pm.moduleName != snap.ModuleName)
                continue;
            if (seen == snap.ModuleOrd)
            {
                pm.moduleRef = newModule;
                newModule.snapshot = pm;
                return;
            }
            seen++;
        }
    }

    static void RunOnInitializePass(Part part, List<PartModule> rebuilt)
    {
        for (int i = 0; i < rebuilt.Count; i++)
        {
            var pm = rebuilt[i];
            if (pm == null)
                continue;
            try
            {
                pm.OnInitialize();
            }
            catch (Exception ex)
            {
                Log.Error($"OnInitialize threw for {pm.moduleName} on part {part.partInfo?.name}");
                Log.LogException(ex);
            }
        }
    }

    static void RunStartPipeline(Part part, List<PartModule> rebuilt)
    {
        if (rebuilt.Count == 0)
            return;

        PartModule.StartState startState;
        try
        {
            startState = part.GetModuleStartState();
        }
        catch (Exception ex)
        {
            Log.Error($"GetModuleStartState threw for part {part.partInfo?.name}");
            Log.LogException(ex);
            return;
        }

        // ModulesOnStart parity (Part.cs:5615): ApplyUpgrades then OnStart.
        for (int i = 0; i < rebuilt.Count; i++)
        {
            var pm = rebuilt[i];
            if (pm == null)
                continue;
            try
            {
                pm.ApplyUpgrades(startState);
            }
            catch (Exception ex)
            {
                Log.Error($"ApplyUpgrades threw for {pm.moduleName} on part {part.partInfo?.name}");
                Log.LogException(ex);
            }
        }

        for (int i = 0; i < rebuilt.Count; i++)
        {
            var pm = rebuilt[i];
            if (pm == null)
                continue;
            try
            {
                pm.OnStart(startState);
            }
            catch (Exception ex)
            {
                Log.Error($"OnStart threw for {pm.moduleName} on part {part.partInfo?.name}");
                Log.LogException(ex);
            }
        }

        // ModulesBeforePartAttachJoint parity (Part.cs:5694).
        for (int i = 0; i < rebuilt.Count; i++)
        {
            var pm = rebuilt[i];
            if (pm == null)
                continue;
            try
            {
                pm.OnStartBeforePartAttachJoint(startState);
            }
            catch (Exception ex)
            {
                Log.Error(
                    $"OnStartBeforePartAttachJoint threw for {pm.moduleName} on part {part.partInfo?.name}"
                );
                Log.LogException(ex);
            }
        }

        // ModulesOnStartFinished parity (Part.cs:5674).
        for (int i = 0; i < rebuilt.Count; i++)
        {
            var pm = rebuilt[i];
            if (pm == null)
                continue;
            try
            {
                pm.OnStartFinished(startState);
            }
            catch (Exception ex)
            {
                Log.Error(
                    $"OnStartFinished threw for {pm.moduleName} on part {part.partInfo?.name}"
                );
                Log.LogException(ex);
            }
            try
            {
                pm.ApplyAdjustersOnStart();
            }
            catch (Exception ex)
            {
                Log.Error(
                    $"ApplyAdjustersOnStart threw for {pm.moduleName} on part {part.partInfo?.name}"
                );
                Log.LogException(ex);
            }
        }
    }

    static int CountSameNamedBefore(PartModuleList modules, string name, int beforeIndex)
    {
        int ord = 0;
        int upper = beforeIndex < modules.Count ? beforeIndex : modules.Count;
        for (int i = 0; i < upper; i++)
        {
            var pm = modules[i];
            if (pm != null && pm.moduleName == name)
                ord++;
        }
        return ord;
    }

    static ConfigNode FindPrefabModuleNode(Part part, string moduleName, int ord)
    {
        var partConfig = part.partInfo?.partConfig;
        if (partConfig == null)
            return null;

        var moduleNodes = partConfig.GetNodes("MODULE");
        if (moduleNodes == null || moduleNodes.Length == 0)
            return null;

        int seen = 0;
        for (int i = 0; i < moduleNodes.Length; i++)
        {
            var n = moduleNodes[i];
            if (n.GetValue("name") != moduleName)
                continue;
            if (seen == ord)
                return n;
            seen++;
        }

        return null;
    }

    static PartModule FindPrefabModule(Part part, string moduleName, int ord)
    {
        var prefab = part.partInfo?.partPrefab;
        if (prefab == null)
            return null;

        int seen = 0;
        for (int i = 0; i < prefab.Modules.Count; i++)
        {
            var pm = prefab.Modules[i];
            if (pm == null || pm.moduleName != moduleName)
                continue;
            if (seen == ord)
                return pm;
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

    static IEnumerable<Part> EnumerateLiveParts()
    {
        if (HighLogic.LoadedSceneIsFlight)
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels == null)
                yield break;
            for (int vi = 0; vi < vessels.Count; vi++)
            {
                var v = vessels[vi];
                if (v?.parts == null)
                    continue;
                for (int pi = 0; pi < v.parts.Count; pi++)
                {
                    var p = v.parts[pi];
                    if (p != null)
                        yield return p;
                }
            }
            yield break;
        }

        if (HighLogic.LoadedSceneIsEditor)
        {
            var ship = EditorLogic.fetch?.ship;
            if (ship?.Parts == null)
                yield break;
            for (int i = 0; i < ship.Parts.Count; i++)
            {
                var p = ship.Parts[i];
                if (p != null)
                    yield return p;
            }
        }
    }

    static Part FindPartByPersistentId(uint persistentId)
    {
        foreach (var p in EnumerateLiveParts())
            if (p.persistentId == persistentId)
                return p;
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
