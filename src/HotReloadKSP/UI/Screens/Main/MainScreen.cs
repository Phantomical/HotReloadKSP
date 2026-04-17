using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    // Selection persists by assembly simple name so it survives list rebuilds
    // (which happen every OnEnable because assemblies may be loaded/reloaded).
    readonly HashSet<string> selected = new();

    internal void BuildUI()
    {
        var content = transform;

        DebugUIManager.CreateHeader(content, "Loaded Assemblies");
        DebugUIManager.CreateSpacer(content, 4f);

        listContainer = DebugUIManager.CreateScrollView(content);

        DebugUIManager.CreateSpacer(content, 4f);

        var btnRow = DebugUIManager.CreateHorizontalLayout(content);
        var reloadButton = DebugUIManager.CreateButton<ReloadButton>(
            btnRow.transform,
            "Reload Selected"
        );
        reloadButton.screen = this;

        statusLabel = DebugUIManager.CreateLabel(content, "");
    }

    void OnEnable()
    {
        if (listContainer == null)
            return;
        RebuildList();
    }

    void OnDisable()
    {
        if (listContainer == null)
            return;
        for (int i = listContainer.childCount - 1; i >= 0; i--)
            Destroy(listContainer.GetChild(i).gameObject);
    }

    void RebuildList()
    {
        for (int i = listContainer.childCount - 1; i >= 0; i--)
            Destroy(listContainer.GetChild(i).gameObject);

        var entries = new List<AssemblyLoader.LoadedAssembly>();
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null)
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
            toggle.isOn = reloadable && selected.Contains(la.name);

            var key = la.name;
            toggle.onValueChanged.AddListener(isOn =>
            {
                if (isOn)
                    selected.Add(key);
                else
                    selected.Remove(key);
            });
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
        foreach (var name in selected)
        {
            var la = FindLoaded(name);
            if (la == null || string.IsNullOrEmpty(la.path) || !File.Exists(la.path))
            {
                Log.Warn($"Skipping {name}: no loaded entry or missing file");
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
                Log.Error($"Reload threw for {name}");
                Log.LogException(ex);
                failed++;
            }
        }

        SetStatus(failed == 0 ? $"Reloaded {ok} assembly(s)." : $"Reloaded {ok}, {failed} failed.");
        RebuildList();
    }

    static AssemblyLoader.LoadedAssembly FindLoaded(string name)
    {
        foreach (var la in AssemblyLoader.loadedAssemblies)
        {
            if (la?.assembly == null)
                continue;
            if (la.name == name)
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
