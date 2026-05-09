using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HotReloadKSP;

/// <summary>
/// Mirrors the null-normalization that Unity's serializer applies during
/// <see cref="UnityEngine.Object.Instantiate(UnityEngine.Object)"/>. When a
/// prefab clone is materialised, Unity routes its fields through serialization
/// and converts null reference-type fields to their canonical defaults
/// (string -> "", List&lt;T&gt; -> empty, T[] -> empty, [Serializable] class
/// with parameterless ctor -> default-constructed). Components built directly
/// via <c>gameObject.AddComponent</c> skip that pipeline, so user code that
/// implicitly relies on those defaults — e.g. <c>if (myString == "")</c> on a
/// string that was never set in config — breaks after a HotReload swap.
///
/// Apply <see cref="Normalize"/> to every component built via AddComponent and
/// then Load'd from a ConfigNode. Safe to call repeatedly: only null reference
/// values are touched.
/// </summary>
internal static class UnitySerializationNormalizer
{
    const BindingFlags FieldFlags =
        BindingFlags.Instance
        | BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Walk the instance's serializable fields up to but not including
    /// <see cref="MonoBehaviour"/> and replace null reference values with the
    /// defaults Unity's serializer would have produced.
    /// </summary>
    public static void Normalize(object instance)
    {
        if (instance == null)
            return;

        // Stop at MonoBehaviour: walking into Behaviour/Component/Object would
        // expose engine-private fields we have no business mutating, and they
        // are also typed as UnityEngine.Object refs so we'd skip them anyway.
        var t = instance.GetType();
        while (t != null && t != typeof(MonoBehaviour) && t != typeof(object))
        {
            FieldInfo[] fields;
            try
            {
                fields = t.GetFields(FieldFlags);
            }
            catch (Exception ex)
            {
                Log.Warn($"GetFields threw for {t.FullName}");
                Log.LogException(ex);
                t = t.BaseType;
                continue;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (!IsUnitySerialized(f))
                    continue;
                NormalizeField(instance, f);
            }
            t = t.BaseType;
        }
    }

    internal static bool IsUnitySerialized(FieldInfo f)
    {
        if (f.IsStatic || f.IsInitOnly || f.IsLiteral)
            return false;
        if (f.IsDefined(typeof(NonSerializedAttribute), inherit: false))
            return false;
        // Public fields auto-serialize; non-public requires [SerializeField].
        return f.IsPublic || f.IsDefined(typeof(SerializeField), inherit: false);
    }

    static void NormalizeField(object instance, FieldInfo f)
    {
        var ft = f.FieldType;

        // Value types can't be null. Primitives, structs, enums - skip.
        if (ft.IsValueType)
            return;

        // UnityEngine.Object references are stored by instance id and Unity
        // does not default-construct them on null - null is a legitimate
        // serialized value. Preserve.
        if (typeof(UnityEngine.Object).IsAssignableFrom(ft))
            return;

        object current;
        try
        {
            current = f.GetValue(instance);
        }
        catch (Exception ex)
        {
            Log.Warn($"GetValue threw for {f.DeclaringType?.FullName}.{f.Name}");
            Log.LogException(ex);
            return;
        }
        if (current != null)
            return;

        object replacement = BuildDefault(ft);
        if (replacement == null)
            return;

        try
        {
            f.SetValue(instance, replacement);
        }
        catch (Exception ex)
        {
            Log.Warn($"SetValue threw for {f.DeclaringType?.FullName}.{f.Name}");
            Log.LogException(ex);
        }
    }

    static object BuildDefault(Type ft)
    {
        if (ft == typeof(string))
            return "";

        if (ft.IsArray)
        {
            var elem = ft.GetElementType();
            if (elem == null)
                return null;
            return Array.CreateInstance(elem, 0);
        }

        if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
        {
            try
            {
                return Activator.CreateInstance(ft);
            }
            catch (Exception ex)
            {
                Log.Warn($"Default List<> instantiation threw for {ft.FullName}");
                Log.LogException(ex);
                return null;
            }
        }

        // [Serializable] custom classes get default-constructed if a
        // parameterless ctor exists. Unity logs a warning and leaves null
        // when there isn't one; we mirror that behaviour silently.
        if (ft.IsClass && ft.IsDefined(typeof(SerializableAttribute), inherit: false))
        {
            var ctor = ft.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null
            );
            if (ctor == null)
                return null;
            try
            {
                return ctor.Invoke(null);
            }
            catch (Exception ex)
            {
                Log.Warn($"Default ctor for {ft.FullName} threw");
                Log.LogException(ex);
                return null;
            }
        }

        return null;
    }
}
