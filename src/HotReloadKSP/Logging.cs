using UnityEngine;

namespace HotReloadKSP;

internal static class Log
{
    private const string Prefix = "[HotReloadKSP] ";

    public static void Info(string msg) => Debug.Log(Prefix + msg);

    public static void Warn(string msg) => Debug.LogWarning(Prefix + msg);

    public static void Error(string msg) => Debug.LogError(Prefix + msg);
}
