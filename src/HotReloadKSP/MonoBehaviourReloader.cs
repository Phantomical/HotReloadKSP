using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HotReloadKSP;

internal static class MonoBehaviourReloader
{
    const BindingFlags HookFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    struct TypeEntry
    {
        public Type Type;
        public MethodInfo Hook;
    }

    struct Swap
    {
        public MonoBehaviour Old;
        public MonoBehaviour New;
        public MethodInfo Hook;
        public string TypeName;
    }

    public static void Reload(Assembly oldAsm, Assembly newAsm)
    {
        var newTypes = BuildNewTypeIndex(newAsm);
        if (newTypes.Count == 0)
            return;

        var candidates = CollectCandidates(oldAsm);
        if (candidates.Count == 0)
            return;

        // Record the pre-swap activeSelf for every parent GameObject once, so that
        // multiple swapped components sharing a GameObject don't stomp each other.
        var originalActive = new Dictionary<GameObject, bool>();
        foreach (var oldComp in candidates)
        {
            if (oldComp == null)
                continue;
            var go = oldComp.gameObject;
            if (go == null)
                continue;
            if (!originalActive.ContainsKey(go))
                originalActive[go] = go.activeSelf;
        }

        foreach (var kv in originalActive)
            SafeSetActive(kv.Key, false, "<batch>");

        var swaps = new List<Swap>();
        foreach (var oldComp in candidates)
        {
            if (oldComp == null)
                continue;

            var oldType = oldComp.GetType();
            if (!newTypes.TryGetValue(oldType.FullName, out var entry))
                continue;

            var go = oldComp.gameObject;
            if (go == null)
                continue;

            var typeName = entry.Type.FullName;
            Component newComp;
            try
            {
                newComp = go.AddComponent(entry.Type);
            }
            catch (Exception ex)
            {
                Log.Error($"AddComponent threw for {typeName}");
                Log.LogException(ex);
                continue;
            }

            if (newComp == null)
            {
                Log.Error($"AddComponent returned null for {typeName}");
                continue;
            }

            swaps.Add(
                new Swap
                {
                    Old = oldComp,
                    New = (MonoBehaviour)newComp,
                    Hook = entry.Hook,
                    TypeName = typeName,
                }
            );
        }

        foreach (var s in swaps)
        {
            try
            {
                s.Hook.Invoke(s.New, [s.Old]);
            }
            catch (TargetInvocationException tie)
            {
                Log.Error($"OnHotReload threw for {s.TypeName}");
                Log.LogException(tie.InnerException ?? tie);
            }
            catch (Exception ex)
            {
                Log.Error($"OnHotReload threw for {s.TypeName}");
                Log.LogException(ex);
            }
        }

        foreach (var kv in originalActive)
            SafeSetActive(kv.Key, kv.Value, "<batch>");

        foreach (var s in swaps)
        {
            try
            {
                UnityEngine.Object.DestroyImmediate(s.Old);
            }
            catch (Exception ex)
            {
                Log.Error($"DestroyImmediate threw for old {s.TypeName}");
                Log.LogException(ex);
            }

            Log.Info($"Hot-reloaded MonoBehaviour {s.TypeName}");
        }
    }

    static void SafeSetActive(GameObject go, bool value, string context)
    {
        if (go == null)
            return;
        try
        {
            go.SetActive(value);
        }
        catch (Exception ex)
        {
            Log.Error($"SetActive({value}) threw during MonoBehaviour reload ({context})");
            Log.LogException(ex);
        }
    }

    static Dictionary<string, TypeEntry> BuildNewTypeIndex(Assembly newAsm)
    {
        Type[] types;
        try
        {
            types = newAsm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var index = new Dictionary<string, TypeEntry>();
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null)
                continue;
            if (t.IsAbstract)
                continue;
            if (!typeof(MonoBehaviour).IsAssignableFrom(t))
                continue;
            if (IsHandledElsewhere(t))
                continue;
            if (t.FullName == null)
                continue;

            var hook = t.GetMethod(
                "OnHotReload",
                HookFlags,
                null,
                [typeof(MonoBehaviour)],
                null
            );
            if (hook == null)
                continue;

            index[t.FullName] = new TypeEntry { Type = t, Hook = hook };
        }
        return index;
    }

    static List<MonoBehaviour> CollectCandidates(Assembly oldAsm)
    {
        var result = new List<MonoBehaviour>();
        var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c == null)
                continue;

            var t = c.GetType();
            if (t.Assembly != oldAsm)
                continue;
            if (IsHandledElsewhere(t))
                continue;

            var go = c.gameObject;
            if (go == null)
                continue;
            if (!go.scene.IsValid())
                continue;

            result.Add(c);
        }
        return result;
    }

    static bool IsHandledElsewhere(Type t)
    {
        return typeof(VesselModule).IsAssignableFrom(t)
            || typeof(PartModule).IsAssignableFrom(t)
            || typeof(ScenarioModule).IsAssignableFrom(t);
    }
}
