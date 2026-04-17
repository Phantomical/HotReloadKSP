using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HotReloadKSP;

internal static class ScenarioModuleReloader
{
    internal struct ModuleSnapshot
    {
        public string ClassName;
        public ConfigNode Node;
        public List<GameScenes> TargetScenes;
        public int Index;
    }

    public static List<ModuleSnapshot> SnapshotAndDetach(Assembly oldAsm)
    {
        var snapshots = new List<ModuleSnapshot>();

        var runner = ScenarioRunner.Instance;
        if (runner == null || runner.modules == null)
            return snapshots;

        var modules = runner.modules;
        for (int i = modules.Count - 1; i >= 0; i--)
        {
            var m = modules[i];
            if (m == null)
                continue;
            if (m.GetType().Assembly != oldAsm)
                continue;

            var node = new ConfigNode("SCENARIO");
            try
            {
                m.Save(node);
            }
            catch (Exception ex)
            {
                Log.Warn($"Save threw for ScenarioModule {m.GetType().FullName}");
                Log.LogException(ex);
            }

            snapshots.Add(
                new ModuleSnapshot
                {
                    ClassName = m.ClassName,
                    Node = node,
                    TargetScenes =
                        m.targetScenes != null
                            ? new List<GameScenes>(m.targetScenes)
                            : new List<GameScenes>(),
                    Index = i,
                }
            );

            modules.RemoveAt(i);
            UnityEngine.Object.DestroyImmediate(m);
        }

        var stray = runner.gameObject.GetComponents<ScenarioModule>();
        for (int k = 0; k < stray.Length; k++)
        {
            var c = stray[k];
            if (c == null)
                continue;
            if (c.GetType().Assembly != oldAsm)
                continue;
            UnityEngine.Object.DestroyImmediate(c);
        }

        return snapshots;
    }

    public static void ReattachAndRestore(List<ModuleSnapshot> snapshots, Assembly newAsm)
    {
        if (snapshots.Count == 0)
            return;

        var runner = ScenarioRunner.Instance;
        if (runner == null)
            return;

        snapshots.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var snap in snapshots)
        {
            ScenarioModule added;
            try
            {
                added = runner.AddModule(snap.Node);
            }
            catch (Exception ex)
            {
                Log.Error($"AddModule threw for ScenarioModule {snap.ClassName}");
                Log.LogException(ex);
                continue;
            }

            if (added == null)
            {
                Log.Warn($"AddModule returned null for ScenarioModule {snap.ClassName}");
                continue;
            }

            added.targetScenes = new List<GameScenes>(snap.TargetScenes);

            int target = Mathf.Clamp(snap.Index, 0, runner.modules.Count - 1);
            MoveToIndex(runner.modules, added, target);

            RelinkProtoModule(runner, snap.ClassName, added);
        }
    }

    static void RelinkProtoModule(ScenarioRunner runner, string className, ScenarioModule added)
    {
        if (runner.protoModules == null)
            return;
        for (int i = 0; i < runner.protoModules.Count; i++)
        {
            var p = runner.protoModules[i];
            if (p == null)
                continue;
            if (p.moduleName != className)
                continue;
            if (p.moduleRef != null && p.moduleRef != added)
                continue;
            p.moduleRef = added;
            added.snapshot = p;
            return;
        }
    }

    static void MoveToIndex(List<ScenarioModule> list, ScenarioModule module, int index)
    {
        int current = list.IndexOf(module);
        if (current < 0 || current == index)
            return;
        if (index < 0)
            index = 0;
        if (index >= list.Count)
            index = list.Count - 1;
        list.RemoveAt(current);
        list.Insert(index, module);
    }
}
