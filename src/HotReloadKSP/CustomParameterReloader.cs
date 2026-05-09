using System;
using System.Collections.Generic;
using System.Reflection;

namespace HotReloadKSP;

internal static class CustomParameterReloader
{
    internal struct ParameterSnapshot
    {
        public GameParameters Owner;
        public string TypeName;
        public ConfigNode Node;
    }

    public static List<ParameterSnapshot> SnapshotAndRemove(Assembly oldAsm)
    {
        var snapshots = new List<ParameterSnapshot>();
        foreach (var gp in EnumerateGameParameters())
            CollectAndRemove(gp, oldAsm, snapshots);
        return snapshots;
    }

    static void CollectAndRemove(
        GameParameters gp,
        Assembly oldAsm,
        List<ParameterSnapshot> snapshots
    )
    {
        if (gp?.customParams == null)
            return;

        List<Type> stale = null;
        foreach (var kvp in gp.customParams)
        {
            if (kvp.Key == null)
                continue;
            if (kvp.Key.Assembly != oldAsm)
                continue;

            var node = new ConfigNode(kvp.Key.Name);
            if (kvp.Value != null)
            {
                try
                {
                    kvp.Value.Save(node);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Save threw for CustomParameterNode {kvp.Key.FullName}");
                    Log.LogException(ex);
                }
            }

            snapshots.Add(
                new ParameterSnapshot
                {
                    Owner = gp,
                    TypeName = kvp.Key.Name,
                    Node = node,
                }
            );

            (stale ??= new List<Type>()).Add(kvp.Key);
        }

        if (stale != null)
        {
            for (int i = 0; i < stale.Count; i++)
                gp.customParams.Remove(stale[i]);
        }
    }

    public static void RegisterAndRestore(
        List<ParameterSnapshot> snapshots,
        Assembly oldAsm,
        Assembly newAsm
    )
    {
        if (GameParameters.ParameterTypes == null)
            return;

        if (oldAsm != null)
            GameParameters.ParameterTypes.RemoveAll(t => t == null || t.Assembly == oldAsm);

        Type[] types;
        try
        {
            types = newAsm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var newTypes = new List<Type>();
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null)
                continue;
            if (t.IsAbstract)
                continue;
            if (!typeof(GameParameters.CustomParameterNode).IsAssignableFrom(t))
                continue;

            newTypes.Add(t);
            if (!GameParameters.ParameterTypes.Contains(t))
                GameParameters.ParameterTypes.Add(t);
        }

        if (snapshots != null)
        {
            for (int i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                var newType = FindByName(newTypes, snap.TypeName);
                if (newType == null)
                {
                    Log.Warn(
                        $"No matching CustomParameterNode for {snap.TypeName} in {newAsm.GetName().Name}; dropping saved state"
                    );
                    continue;
                }

                GameParameters.CustomParameterNode instance;
                try
                {
                    instance = (GameParameters.CustomParameterNode)
                        Activator.CreateInstance(newType);
                }
                catch (Exception ex)
                {
                    Log.Error($"Activator.CreateInstance threw for {newType.FullName}");
                    Log.LogException(ex);
                    continue;
                }

                try
                {
                    instance.Load(snap.Node);
                }
                catch (Exception ex)
                {
                    Log.Error($"Load threw for CustomParameterNode {newType.FullName}");
                    Log.LogException(ex);
                }

                snap.Owner.customParams[newType] = instance;
            }
        }

        // Mirrors GameParameters..ctor: every type in ParameterTypes gets a
        // default instance in customParams. Backfill any new-assembly type that
        // wasn't restored from a snapshot so freshly-introduced parameter
        // classes pick up their default values across all GameParameters.
        foreach (var gp in EnumerateGameParameters())
        {
            if (gp?.customParams == null)
                continue;
            for (int i = 0; i < newTypes.Count; i++)
            {
                var t = newTypes[i];
                if (gp.customParams.ContainsKey(t))
                    continue;
                try
                {
                    gp.customParams[t] = (GameParameters.CustomParameterNode)
                        Activator.CreateInstance(t);
                }
                catch (Exception ex)
                {
                    Log.Error($"Activator.CreateInstance threw for {t.FullName}");
                    Log.LogException(ex);
                }
            }
        }

        SortParameterTypesByTitle();
    }

    static Type FindByName(List<Type> types, string name)
    {
        for (int i = 0; i < types.Count; i++)
        {
            if (types[i].Name == name)
                return types[i];
        }
        return null;
    }

    static void SortParameterTypesByTitle()
    {
        GameParameters titleSource = HighLogic.CurrentGame?.Parameters;
        if (titleSource == null && GameParameters.DifficultyPresets != null)
        {
            foreach (var kvp in GameParameters.DifficultyPresets)
            {
                if (kvp.Value != null)
                {
                    titleSource = kvp.Value;
                    break;
                }
            }
        }

        if (titleSource?.customParams == null)
            return;

        GameParameters.ParameterTypes.Sort(
            (a, b) =>
            {
                titleSource.customParams.TryGetValue(a, out var na);
                titleSource.customParams.TryGetValue(b, out var nb);
                var ta = na?.Title ?? "";
                var tb = nb?.Title ?? "";
                return ta.CompareTo(tb);
            }
        );
    }

    static IEnumerable<GameParameters> EnumerateGameParameters()
    {
        var current = HighLogic.CurrentGame?.Parameters;
        if (current != null)
            yield return current;

        if (GameParameters.DifficultyPresets != null)
        {
            foreach (var kvp in GameParameters.DifficultyPresets)
            {
                if (kvp.Value != null && kvp.Value != current)
                    yield return kvp.Value;
            }
        }
    }
}
