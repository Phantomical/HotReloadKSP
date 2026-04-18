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
        public SafeField[] SafeFields;
    }

    internal struct SafeField
    {
        public FieldInfo Info;

        // True if FieldType is a MonoBehaviour subclass in the reloading assembly;
        // such references must be rewritten to the corresponding new component.
        public bool Remap;
    }

    internal struct Swap
    {
        public MonoBehaviour Old;
        public MonoBehaviour New;
        public MethodInfo Hook;
        public SafeField[] NewFields;
        public FieldInfo[] OldFields;
        public string TypeName;
    }

    internal readonly struct Pending
    {
        internal readonly List<Swap> Swaps;
        internal readonly Dictionary<GameObject, bool> OriginalActive;

        internal Pending(List<Swap> swaps, Dictionary<GameObject, bool> originalActive)
        {
            Swaps = swaps;
            OriginalActive = originalActive;
        }

        internal static Pending Empty => new(new List<Swap>(), new Dictionary<GameObject, bool>());
    }

    /// <summary>
    /// Attach replacement components on inactive parent GameObjects, copy fields
    /// across, and invoke each type's optional instance <c>OnHotReload</c> hook.
    /// Parents stay inactive and old components stay alive until
    /// <see cref="FinalizeReload"/> runs.
    /// </summary>
    internal static Pending PrepareReload(Assembly oldAsm, Assembly newAsm)
    {
        var newTypes = BuildNewTypeIndex(newAsm);
        if (newTypes.Count == 0)
            return Pending.Empty;

        var candidates = CollectCandidates(oldAsm);
        if (candidates.Count == 0)
            return Pending.Empty;

        var oldFieldCache = new Dictionary<Type, FieldInfo[]>();

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
            Component newCompRaw;
            try
            {
                newCompRaw = go.AddComponent(entry.Type);
            }
            catch (Exception ex)
            {
                Log.Error($"AddComponent threw for {typeName}");
                Log.LogException(ex);
                continue;
            }

            if (newCompRaw == null)
            {
                Log.Error($"AddComponent returned null for {typeName}");
                continue;
            }

            // Keep the new component disabled across reactivation so OnEnable
            // only fires after the old component has been destroyed. Otherwise
            // the old component's OnDisable during DestroyImmediate can undo
            // whatever the new component's OnEnable just set up (self-reload
            // scenarios where the UI component is on the new assembly).
            var newComp = (MonoBehaviour)newCompRaw;
            newComp.enabled = false;

            swaps.Add(
                new Swap
                {
                    Old = oldComp,
                    New = newComp,
                    Hook = entry.Hook,
                    NewFields = entry.SafeFields,
                    OldFields = GetOldFields(oldType, entry.SafeFields, oldFieldCache),
                    TypeName = typeName,
                }
            );
        }

        var remap = new Dictionary<MonoBehaviour, MonoBehaviour>(swaps.Count);
        foreach (var s in swaps)
            remap[s.Old] = s.New;

        foreach (var s in swaps)
            CopyFields(s, remap);

        foreach (var s in swaps)
        {
            if (s.Hook == null)
                continue;
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

        return new Pending(swaps, originalActive);
    }

    /// <summary>
    /// Restore each parent GameObject's original <c>activeSelf</c>, destroy the old
    /// components left behind by <see cref="PrepareReload"/>, then enable the new
    /// components so their <c>OnEnable</c> runs only after the matching old
    /// component's <c>OnDisable</c>/<c>OnDestroy</c> have finished.
    /// </summary>
    internal static void FinalizeReload(Pending pending)
    {
        foreach (var kv in pending.OriginalActive)
            SafeSetActive(kv.Key, kv.Value, "<batch>");

        foreach (var s in pending.Swaps)
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
        }

        foreach (var s in pending.Swaps)
        {
            if (s.New == null)
                continue;
            try
            {
                s.New.enabled = true;
            }
            catch (Exception ex)
            {
                Log.Error($"Enabling new {s.TypeName} threw");
                Log.LogException(ex);
            }

            Log.Info($"Hot-reloaded MonoBehaviour {s.TypeName}");
        }
    }

    static void CopyFields(Swap s, Dictionary<MonoBehaviour, MonoBehaviour> remap)
    {
        for (int i = 0; i < s.NewFields.Length; i++)
        {
            var oldField = s.OldFields[i];
            if (oldField == null)
                continue;
            var safe = s.NewFields[i];
            try
            {
                var value = oldField.GetValue(s.Old);
                if (safe.Remap && value != null)
                {
                    var oldRef = (MonoBehaviour)value;
                    if (remap.TryGetValue(oldRef, out var newRef))
                    {
                        value = newRef;
                    }
                    else
                    {
                        Log.Warn(
                            $"Field {safe.Info.Name} on {s.TypeName} referenced an unswapped {oldRef.GetType().FullName}; setting to null"
                        );
                        value = null;
                    }
                }
                safe.Info.SetValue(s.New, value);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to copy field {safe.Info.Name} on {s.TypeName}");
                Log.LogException(ex);
            }
        }
    }

    static FieldInfo[] GetOldFields(
        Type oldType,
        SafeField[] newFields,
        Dictionary<Type, FieldInfo[]> cache
    )
    {
        if (cache.TryGetValue(oldType, out var cached))
            return cached;

        var result = new FieldInfo[newFields.Length];
        for (int i = 0; i < newFields.Length; i++)
            result[i] = FindInstanceField(oldType, newFields[i].Info.Name);

        cache[oldType] = result;
        return result;
    }

    static FieldInfo FindInstanceField(Type t, string name)
    {
        const BindingFlags flags =
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        while (t != null && t != typeof(MonoBehaviour))
        {
            var f = t.GetField(name, flags);
            if (f != null)
                return f;
            t = t.BaseType;
        }
        return null;
    }

    static SafeField[] CollectSafeFields(Type newType, Assembly reloadingAsm)
    {
        const BindingFlags flags =
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        var fields = new List<SafeField>();
        var t = newType;
        while (t != null && t != typeof(MonoBehaviour))
        {
            foreach (var f in t.GetFields(flags))
            {
                if (f.IsStatic)
                    continue;

                var ft = f.FieldType;
                if (InvolvesAssembly(ft, reloadingAsm))
                {
                    // A direct MonoBehaviour-typed reference to a reloading type can be
                    // remapped to the new component; anything else (non-MonoBehaviour
                    // reloading types, containers of reloading types) stays skipped.
                    if (ft.Assembly == reloadingAsm && typeof(MonoBehaviour).IsAssignableFrom(ft))
                        fields.Add(new SafeField { Info = f, Remap = true });
                    continue;
                }

                fields.Add(new SafeField { Info = f, Remap = false });
            }
            t = t.BaseType;
        }
        return fields.ToArray();
    }

    static bool InvolvesAssembly(Type t, Assembly asm)
    {
        if (t == null)
            return false;
        if (t.Assembly == asm)
            return true;
        if (t.HasElementType)
            return InvolvesAssembly(t.GetElementType(), asm);
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                if (InvolvesAssembly(arg, asm))
                    return true;
        }
        return false;
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

            var hook = t.GetMethod("OnHotReload", HookFlags, null, [typeof(MonoBehaviour)], null);

            index[t.FullName] = new TypeEntry
            {
                Type = t,
                Hook = hook,
                SafeFields = CollectSafeFields(t, newAsm),
            };
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
