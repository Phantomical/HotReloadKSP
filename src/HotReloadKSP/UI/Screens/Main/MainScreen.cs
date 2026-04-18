using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HotReloadKSP.UI.Screens.Main;

internal class MainScreenContent : MonoBehaviour
{
    [SerializeField]
    Transform listContainer;

    [SerializeField]
    TextMeshProUGUI statusLabel;

    [SerializeField]
    Toggle autoReload;

    readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);

    internal void BuildUI()
    {
        var content = transform;

        DebugUIManager.CreateHeader(content, "Loaded Assemblies");
        DebugUIManager.CreateSpacer(content, 4f);

        listContainer = DebugUIManager.CreateScrollView(content);

        DebugUIManager.CreateSpacer(content, 4f);

        autoReload = DebugUIManager.CreateToggle(content, "Auto-reload on file change");
        var btnRow = DebugUIManager.CreateHorizontalLayout(content);
        var reloadButton = DebugUIManager.CreateButton<ReloadButton>(
            btnRow.transform,
            "Reload Selected"
        );
        reloadButton.screen = this;

        statusLabel = DebugUIManager.CreateLabel(content, "");
    }

    void Start()
    {
        autoReload.onValueChanged.AddListener(OnAutoReloadToggle);
    }

    void OnDestroy()
    {
        autoReload.onValueChanged.RemoveListener(OnAutoReloadToggle);
    }

    void OnEnable()
    {
        if (listContainer == null)
            return;
        RebuildList();
        var manager = AutoReloadManager.Instance;
        if (manager != null)
            manager.enabled = autoReload.isOn;
    }

    void OnDisable()
    {
        if (listContainer == null)
            return;
        ClearList();
    }

    void OnAutoReloadToggle(bool value)
    {
        var manager = AutoReloadManager.Instance;
        if (manager == null)
            return;

        manager.enabled = value;
    }

    void RebuildList()
    {
        // Post-self-reload, ReloadSelected keeps running on this destroyed old
        // instance and calls RebuildList again; the replacement's OnEnable has
        // already populated the list, so bail before ClearList wipes it.
        if (this == null)
            return;

        ClearList();

        var entries = new List<AssemblyLoader.LoadedAssembly>();
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null)
                continue;
            if (!IsHotReloadable(la.assembly))
                continue;
            entries.Add(la);
        }
        entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        foreach (var la in entries)
        {
            bool reloadable = !string.IsNullOrEmpty(la.path) && File.Exists(la.path);
            var label = reloadable ? la.name : $"{la.name}  (no file)";
            var toggle = DebugUIManager.CreateToggle(listContainer, label);

            toggle.interactable = reloadable;

            if (reloadable)
            {
                var key = Path.GetFullPath(la.path);
                toggle.isOn = selected.Contains(key);

                toggle.onValueChanged.AddListener(isOn =>
                {
                    if (isOn)
                        selected.Add(key);
                    else
                        selected.Remove(key);

                    AutoReloadManager.Instance?.SetSelected(selected);
                });
            }
        }
    }

    // Toggle.OnDisable/OnDestroy during parent SetActive(false) or Destroy can
    // synchronously fire onValueChanged(false), which would run our listener and
    // drop the entry from the manager's selection mid-reload. Strip listeners
    // before the child goes away so the backing set survives the rebuild.
    void ClearList()
    {
        for (int i = listContainer.childCount - 1; i >= 0; i--)
        {
            var child = listContainer.GetChild(i).gameObject;
            var toggle = child.GetComponentInChildren<Toggle>(includeInactive: true);
            toggle?.onValueChanged.RemoveAllListeners();
            Destroy(child);
        }
    }

    internal void ReloadSelected()
    {
        if (selected.Count == 0)
        {
            SetStatus("No assemblies selected.");
            return;
        }

        int ok = 0;
        int failed = 0;
        foreach (var path in selected)
        {
            var la = FindLoadedByPath(path);
            if (la == null || string.IsNullOrEmpty(la.path) || !File.Exists(la.path))
            {
                Log.Warn($"Skipping {path}: no loaded entry or missing file");
                failed++;
                continue;
            }

            try
            {
                HotReload.Reload(la.path);
                ok++;
            }
            catch (Exception ex)
            {
                Log.Error($"Reload threw for {la.name}");
                Log.LogException(ex);
                failed++;
            }
        }

        SetStatus(failed == 0 ? $"Reloaded {ok} assembly(s)." : $"Reloaded {ok}, {failed} failed.");
        RebuildList();
    }

    static bool IsHotReloadable(Assembly asm)
    {
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == "HotReload" && attr.Value == "true")
                return true;
        }
        return false;
    }

    static AssemblyLoader.LoadedAssembly FindLoadedByPath(string path)
    {
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null || string.IsNullOrEmpty(la.path))
                continue;
            if (string.Equals(Path.GetFullPath(la.path), path, StringComparison.OrdinalIgnoreCase))
                return la;
        }
        return null;
    }

    void SetStatus(string text)
    {
        if (statusLabel != null)
            statusLabel.text = text;
    }
}
