using System;
using System.Reflection;

namespace HotReloadKSP;

internal static class AssemblySwap
{
    internal struct Result
    {
        public AssemblyLoader.LoadedAssembly LoadedAssembly;
        public Assembly OldAssembly;
        public Assembly NewAssembly;
        public bool WasFirstLoad;
    }

    public static Result Swap(Assembly newAsm) => Swap(newAsm, newAsm.GetName().Name, null);

    public static Result Swap(Assembly newAsm, string targetName, string path)
    {
        // Match on LoadedAssembly.name, not la.assembly.GetName().Name: after a
        // prior reload the runtime Assembly's simple name is the rewritten
        // "-HR<guid>" form, while la.name keeps the original DLL file name.
        AssemblyLoader.LoadedAssembly existing = null;
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly != null && la.name == targetName)
            {
                existing = la;
                break;
            }
        }

        if (existing == null)
        {
            if (string.IsNullOrEmpty(path))
                path = SafeLocation(newAsm);
            var fresh = new AssemblyLoader.LoadedAssembly(newAsm, path, path, null);
            PopulateTypeIndexes(fresh, newAsm);
            AssemblyLoader.loadedAssemblies.Add(fresh);
            return new Result
            {
                LoadedAssembly = fresh,
                OldAssembly = null,
                NewAssembly = newAsm,
                WasFirstLoad = true,
            };
        }

        var oldAsm = existing.assembly;
        existing.assembly = newAsm;
        existing.types.Clear();
        existing.typesDictionary.Clear();
        PopulateTypeIndexes(existing, newAsm);

        return new Result
        {
            LoadedAssembly = existing,
            OldAssembly = oldAsm,
            NewAssembly = newAsm,
            WasFirstLoad = false,
        };
    }

    private static string SafeLocation(Assembly asm)
    {
        // Assemblies loaded from bytes report an empty Location, which
        // LoadedAssembly's constructor rejects via Path.GetDirectoryName.
        string location = null;
        try
        {
            location = asm.Location;
        }
        catch { }

        return string.IsNullOrEmpty(location) ? asm.GetName().Name + ".dll" : location;
    }

    private static void PopulateTypeIndexes(AssemblyLoader.LoadedAssembly la, Assembly asm)
    {
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var loadedTypes = AssemblyLoader.loadedTypes;
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null)
                continue;
            for (int j = 0; j < loadedTypes.Count; j++)
            {
                var baseType = loadedTypes[j];
                if (t == baseType || t.IsSubclassOf(baseType))
                {
                    la.types.Add(baseType, t);
                    la.typesDictionary.Add(baseType, t);
                }
            }
        }
    }
}
