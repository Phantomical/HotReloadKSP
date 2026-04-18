using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HotReloadKSP;

/// <summary>
/// Singleton MonoBehaviour that owns the "which assemblies are hot-reload targets"
/// selection and runs one <see cref="FileSystemWatcher"/> per unique parent directory
/// while enabled. The file-watcher lifecycle tracks <see cref="Behaviour.enabled"/>:
/// OnEnable spins watchers up, OnDisable tears them down. The UI toggles
/// <c>enabled</c> to turn auto-reload on/off.
///
/// Starts disabled in <see cref="Awake"/> so a fresh session is off until the UI
/// re-enables it. On a HotReloadKSP self-reload, MonoBehaviourReloader copies the
/// <c>selected</c> set into the replacement instance; <c>enabled</c> is reset by
/// the reload and is re-applied by <see cref="UI.Screens.Main.MainScreenContent"/>
/// on its own startup path.
/// </summary>
[KSPAddon(KSPAddon.Startup.MainMenu, once: true)]
internal class AutoReloadManager : MonoBehaviour
{
    public static AutoReloadManager Instance { get; private set; }

    // We don't want to reload immediately because there are usually multiple
    // events emitted as part of a file write. By waiting 0.5s after the last
    // event we can reduce the amount of reloads by quite a bit.
    const int DebounceMs = 500;

    // This gets used from multiple threads concurrently. Don't modify it,
    // instead overwrite the whole variable with an updated hashset.
    HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);

    readonly ConcurrentQueue<string> events = [];

    public bool IsSelected(string path) => selected.Contains(path);

    public void SetSelected(HashSet<string> selected)
    {
        this.selected = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
    }

    void OnHotUnload(MonoBehaviour _new)
    {
        Instance = null;
    }

    void Awake()
    {
        Instance = this;
        enabled = false;

        DontDestroyOnLoad(gameObject);
    }

    void OnEnable()
    {
        events.Clear();
        CreateWatcher();
    }

    void OnDisable()
    {
        watcher?.Dispose();
        watcher = null;
    }

    void OnDestroy()
    {
        watcher?.Dispose();
        watcher = null;
    }

    void Update()
    {
        while (events.TryDequeue(out var path))
            ReloadByPath(path);
    }

    void ReloadByPath(string path)
    {
        var la = FindLoadedByPath(path);
        if (la == null || !selected.Contains(path) || !File.Exists(path))
            return;

        try
        {
            HotReload.Reload(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Auto-reload failed for {path}");
            Log.LogException(ex);
        }
    }

    static AssemblyLoader.LoadedAssembly FindLoadedByPath(string path)
    {
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null || string.IsNullOrEmpty(la.path))
                continue;
            if (string.Equals(la.path, path, StringComparison.OrdinalIgnoreCase))
                return la;
        }
        return null;
    }

    #region File Watching
    class FileDebounceState
    {
        public Task task;
        public CancellationTokenSource source;
    }

    readonly object debounceLock = new();
    readonly Dictionary<string, FileDebounceState> debounceStates = [];
    FileSystemWatcher watcher;

    void CreateWatcher()
    {
        var dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
        try
        {
            watcher = new FileSystemWatcher(dir, "*.dll")
            {
                NotifyFilter =
                    NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = true,
            };

            // Build tools commonly replace a DLL by writing to a temp file and
            // renaming it over the target, or by delete-then-create. Subscribe
            // to all three so we catch any of those patterns.
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to create FileSystemWatcher for {dir}");
            Log.LogException(ex);

            watcher?.Dispose();
            watcher = null;
        }
    }

    void OnFileEvent(object _, FileSystemEventArgs e)
    {
        var path = Path.GetFullPath(e.FullPath);

        if (!selected.Contains(path))
            return;

        var cts = new CancellationTokenSource();
        var task = WaitForDebounce(path, cts.Token);

        lock (debounceLock)
        {
            if (!debounceStates.TryGetValue(path, out var state))
            {
                state = new();
                debounceStates.Add(path, state);
            }

            state.source?.Cancel();
            state.source = cts;
            state.task = task;
        }
    }

    async Task WaitForDebounce(string path, CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        events.Enqueue(path);
    }
    #endregion
}
