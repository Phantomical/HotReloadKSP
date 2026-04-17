# Hot-Reload Feasibility & Design — KSP 1.x

Scope: reloading downstream mod assemblies at runtime, covering `PartModule`, `VesselModule`, `ScenarioModule`, `[KSPAddon]` MonoBehaviours, and arbitrary `MonoBehaviour` types.

---

## 1. Bottom line

Technically viable, but with one hard constraint and several awkward ones.

KSP runs on Mono with the old .NET 3.5/4.x profile, which **cannot unload assemblies once loaded**. You can *load new versions* but the old types stay in memory forever. So "hot-reload" here really means "load v2 alongside v1, migrate live instances to v2, and orphan v1." Memory grows each reload cycle — that's a constraint to accept, not fix. Collectible `AssemblyLoadContext` is .NET Core only and not available here.

---

## 2. What works in our favor

The architecture is surprisingly reload-friendly:

- Module type names are **strings in ConfigNodes**, resolved at runtime via `AssemblyLoader.GetClassByName` — so save data is not bound to a specific assembly version.
- All four module types have ConfigNode `Save`/`Load` round-tripping:
  - `PartModule.cs:756` (`Save`), `787` (`Load`)
  - `VesselModule.cs:61-71`
  - `ScenarioModule` via `ProtoScenarioModule.cs:14-22`
- Instance storage is discoverable: `Part.modules`, `Vessel.vesselModules`, `ScenarioRunner.modules`, and scene GameObjects for `[KSPAddon]`.
- Dependency metadata (`KSPAssembly` / `KSPAssemblyDependency`) is already extracted via Mono.Cecil and topo-sorted (`AssemblyLoader.SortAssemblies`, line 150).

---

## 3. Key touch points in the decompiled source

### AssemblyLoader (`AssemblyLoader.cs`)
- `static LoadedAssembyList loadedAssemblies` (line 507) — master assembly list.
- `static Dictionary<Type, List<Type>> subclassesOfParentClass` (line 513) — cached subclass lookup for `PartModule`/`VesselModule`/`ScenarioModule`. **Never cleared.**
- `LoadedAssembly.typesDictionary` (line 247) — name→Type lookup populated during `LoadAssemblies` (line 901), consumed by `GetClassByName` (line 644).
- `Unload()` (line 413) nulls the reference but **does not remove** the entry from the list.

### AddonLoader / KSPAddon
- `AddonLoader.StartAddons(KSPAddon.Startup)` (AddonLoader.cs:81) scans `[KSPAddon]` each scene transition; `StartAddon` instantiates the MonoBehaviour via `gameObject.AddComponent(type)` (line 130).
- `loadedOnceList` (line 22) tracks "once" addons to prevent re-instantiation.

### PartLoader & Part
- `PartLoader` builds a prefab `Part` per `AvailablePart` during startup; each flight instance is an `Instantiate` of that prefab. Prefabs hold **live `PartModule` components** — the reloaded `Type` must replace the component on the prefab, not just on flight instances.
- `Part.AddModule(string)` (Part.cs:5787) resolves type via `AssemblyLoader.GetClassByName(typeof(PartModule), name)` and `AddComponent`s it.
- `Part.cachedModules` (line 657) — per-instance `Dictionary<Type, PartModule>` cache; must be cleared when swapping.

### PartModule
- `static Dictionary<Type, ReflectedAttributes> reflectedAttributeCache` (PartModule.cs:101) — reflection cache keyed by `Type`; holds old-assembly method/field tokens after reload.
- `Save`/`Load` serialize `KSPField` state, events/actions, and dispatch to virtual `OnSave`/`OnLoad`.

### VesselModule
- `VesselModuleManager.modules` (line 23) — static registry, populated once by `CompileModules()` (line 27) via `AssemblyLoader.loadedAssemblies.TypeOperation`. No re-compile path exists today.
- `VesselModuleManager.AddModulesToVessel` (line 143) iterates the registry and `AddComponent`s each active module onto a vessel.
- `Vessel.LoadVesselModules` (Vessel.cs:1473) loads state into *existing* module instances — does not instantiate new ones.

### ScenarioModule
- `KSPScenarioType.GetAllScenarioTypesInAssemblies` (line 18) discovers `[KSPScenario]` types by name (first definition wins).
- `ScenarioRunner.OnGameSceneLoadRequested` (line 35) already `DestroyImmediate`s all scenarios on scene transition — this window is a natural reload boundary.
- `ScenarioRunner.AddModule(ConfigNode)` performs name-based type lookup + `AddComponent` + `Load`.

### Other static caches to invalidate
- `Part.reflectedAttributeCache` (Part.cs:655)
- `BaseFieldList.reflectedAttributeCache` (BaseFieldList.cs:71)
- `BaseActionList.reflectedAttributeCache` (BaseActionList.cs:43)

Miss any of these and you will silently re-instantiate the old Type, or get `MissingMethodException`/`InvalidCastException` later.

---

## 4. Pitfalls (general, apply to every reload mode)

1. **Assemblies don't unload.** Old types, generic instantiations, and JIT code are retained for the process lifetime. A rapid-iteration dev loop is fine; a long session with dozens of reloads will bloat memory.

2. **Delegate/event subscriptions root the old instance.** `GameEvents.onX.Add(oldInstance.Handler)` keeps both the old instance and — transitively — its whole assembly alive for gameplay purposes, even after you destroy the MonoBehaviour. Unsubscribing must be explicit. Many mods don't unsubscribe cleanly in `OnDestroy`; you will need a sweep of `GameEvents` and common subscribable hooks.

3. **Harmony patches survive.** Patches from the old assembly remain in the method patch tables. Unpatch by assembly (`Harmony.UnpatchAll(harmonyId)`) **before** destroying instances, then let the re-loaded assembly re-patch in its new addon startup. Handle the case where Harmony is loaded reflectively (it usually is in KSP).

4. **Dependency cascade.** If mod B declares `KSPAssemblyDependency` on A, reloading A invalidates every Type B has cached that references A. The simplest model: reloading A forces B to reload too. Use `AssemblyLoader.SortAssemblies` / the dependency edges to compute a reload set.

5. **Type identity doesn't match across reload.** `typeof(Foo)` in old-B does not equal `typeof(Foo)` in new-A. Interop between two mods across a partial reload boundary is the main failure mode. Reload dependents together to avoid it.

6. **`[KSPField]` is the only reliably round-trippable state.** Non-`KSPField` runtime fields, cached component references, coroutines, dictionaries built in `OnStart`, input locks — none of these are in the ConfigNode. Either require mods to put durable state in `OnSave`/`OnLoad`, or accept that "reload" implies "re-initialize from scratch."

7. **Prefab components vs. flight-instance components.** The `Part` prefab under `PartLoader` holds a `PartModule` component of the old Type; if you only replace components on active flight parts, the next `Instantiate` still clones the old Type. Replace on the prefab too.

8. **Name collisions.** `KSPScenarioType.GetAllScenarioTypesInAssemblies` picks the first definition per name. After reload, both old-A and new-A have a type with the same name. You must remove old-A's types from `typesDictionary` and the subclass cache, or iteration order will bite you.

9. **KSPField reflection tokens.** `BaseFieldList` stores `FieldInfo` references. Reflection tokens from a different assembly-version are not substitutable; the cache must be rebuilt, not merged.

10. **`DontDestroyOnLoad` leaks.** `[KSPAddon]` with scene `Instantly` or `MainMenu` plus `DontDestroyOnLoad` instances must be explicitly destroyed or they double up after reload.

---

## 5. Scene-boundary reload (the easy case)

Recommended starting point — roughly 20% of the work for 80% of the dev-loop value.

The game is already doing most of the work at scene transitions:
- `ScenarioRunner` destroys and rebuilds all scenario modules (`ScenarioRunner.cs:35-46`).
- `AddonLoader.StartAddons` re-runs addon discovery for the target scene.
- No active flight physics state to preserve.

**Flow:**
1. Wait for `GameEvents.onGameSceneLoadRequested` — the "pending scene change" window.
2. Quiesce: block the scene load briefly (spin a coroutine on a loading-screen object).
3. Unpatch Harmony for the reload set.
4. Swap the DLL(s) in `loadedAssemblies`; re-run the Mono.Cecil dependency extraction; re-run `TypeOperation` scanning.
5. Invalidate all static caches (list in §3).
6. Re-run `VesselModuleManager.CompileModules()` and `KSPScenarioType.GetAllScenarioTypesInAssemblies()`.
7. Rebuild part prefab `PartModule` components for affected part types.
8. Release the scene load. `ScenarioRunner`, `AddonLoader`, and vessel loading will rebuild everything with the new Types.

Vessels persist in the save; on the next vessel-load, `ProtoPartSnapshot` / `ProtoPartModuleSnapshot` deserialization resolves type names via the refreshed caches and creates instances of the new Type. No special handling needed for vessels not currently loaded into scene.

---

## 6. Mid-flight reload (expanded)

Mid-flight means: flight scene active, at least one vessel loaded, physics running, coroutines running, `FlightIntegrator` iterating, `TimingManager` callbacks firing, UI PAWs possibly open. This is the hard mode.

### 6.1 Why it's hard, concretely

- **Physics is stateful and continuous.** `PartJoint` / `ConfigurableJoint` / `Rigidbody` state, integrator accumulators (drag, heat), in-progress resource flows, active `ModuleEngines` combustion state — none of this is in `[KSPField]`. Destroying a PartModule that owns resource handlers or FX objects mid-integration corrupts the vessel.
- **Coroutines are tied to MonoBehaviour lifetime.** `DestroyImmediate` on a module kills its coroutines silently. Many stock and modded modules run long-lived coroutines for deployment, animation, experiment cooldown.
- **Delegate webs are dense.** A single `PartModule` might be subscribed to `GameEvents.onVesselChange`, `onPartUndock`, `onStageActivate`, `Part.OnJustAboutToBeDestroyed`, plus `TimingManager` pre/fixed/late callbacks, plus `Part.OnEditorAttach` analogues. All must be torn down and re-registered.
- **UI references.** Open `UIPartActionWindow` (PAW) holds `UIPartActionItem` instances bound to specific `BaseField`/`BaseEvent` objects on the module. Those fields become dangling; the PAW needs to be closed and rebuilt.
- **Active input locks.** `InputLockManager` lock tokens are keyed by string; a reloaded module's tokens may not match. Leftover locks freeze the UI.
- **`FXModule*` visual effects.** Particles, lights, audio — all Unity components, many held as field references. Must be detached from the module before destroy or Unity leaks them.
- **KSPAddon MonoBehaviours running `Update`/`FixedUpdate` right now.** The swap needs to happen between ticks.

### 6.2 Design: the five-phase swap

Execute the entire swap inside a single `Coroutine` that yields on `WaitForEndOfFrame` at phase boundaries. Set `Time.timeScale = 0` before phase 1 and restore it after phase 5. That suspends `FixedUpdate` and `Update` across the critical window.

#### Phase 1 — Freeze and snapshot

1. `Time.timeScale = 0`; record previous value.
2. For each `Vessel` currently loaded:
   - `vessel.GoOnRails()` to halt physics integration and convert to on-rails state. (Tradeoff: this briefly interrupts the orbital sim, but it's the only clean way to stop the integrator without desync.)
   - Record which vessels were off-rails so we can restore.
3. Close all `UIPartActionWindow` instances via `UIPartActionController.Instance.UpdateFlight` pause path — or more bluntly, `UIPartActionController.Instance.Deactivate()` then `Activate()` at the end.
4. Snapshot each affected `PartModule` on each loaded vessel's parts into a `ConfigNode` via `module.Save(node)`; store `{ partPersistentId, moduleIndex, moduleName, node }`. Also snapshot vessel modules (`vessel.vesselModules`) and scenario modules (`ScenarioRunner.Instance.GetLoadedModules()`).
5. Snapshot `[KSPAddon]` state: most addons don't serialize, but expose an opt-in hook (e.g., scan for a `[HotReloadPersist]` attribute on static fields or an `IHotReloadable` interface with `OnBeforeReload(ConfigNode)` / `OnAfterReload(ConfigNode)`).

#### Phase 2 — Detach

1. Unsubscribe everything we can reach. For each doomed instance:
   - If the instance implements `IHotReloadable`, call its `OnBeforeReload`.
   - Otherwise, rely on `OnDestroy` running — but see pitfall #2; this is not reliable for `GameEvents`.
   - **Scorched-earth fallback:** walk every `GameEvents.*` field (it's a `Dictionary<string, BaseGameEvent>`; see `GameEvents.cs`) and remove any `EventData<T>.OnEvent` delegate whose `Target`'s type comes from a doomed assembly. This requires reflection into `EventData` internals but is the only generic solution.
2. Harmony unpatch: call `Harmony.UnpatchAll(harmonyId)` for each harmony ID from the reload set. Detect by scanning loaded `HarmonyInstance` / `Harmony` singletons; or require mods to register their IDs.
3. For each affected `PartModule`:
   - Remove from `part.Modules` (use `PartModuleList` internals — there's no public remove, so either rebuild the list or reflect).
   - Null out `part.cachedModules` (Part.cs:657).
   - `UnityEngine.Object.DestroyImmediate(module)` — use `DestroyImmediate`, not `Destroy`, so the component is gone before we re-add.
4. For each affected `VesselModule`:
   - Remove from `vessel.vesselModules`.
   - `DestroyImmediate`.
5. For each affected `ScenarioModule`: remove from `ScenarioRunner.Instance.modules`, `DestroyImmediate`.
6. For each affected `[KSPAddon]` MonoBehaviour: `DestroyImmediate`. Remove matching entry from `AddonLoader.loadedOnceList` if present.
7. For each affected part prefab (under `PartLoader.Instance.transform`): destroy the old-type `PartModule` component on the prefab.

#### Phase 3 — Swap assemblies and invalidate caches

1. Load new DLLs via `Assembly.LoadFrom` (or `LoadFile`; `LoadFrom` respects probing). Append to `AssemblyLoader.loadedAssemblies` as `LoadedAssembly` entries.
2. For each reloaded assembly, remove the old entry from `loadedAssemblies` (it will leak in-process, but remove from the lookup list so name resolution hits the new Type first).
3. Rebuild:
   - `AssemblyLoader.subclassesOfParentClass.Clear()` (line 513) — next `GetSubclassesOfParentClass` call repopulates.
   - Each `LoadedAssembly.typesDictionary` — rescan via the same code path as `LoadAssemblies` (line 862).
   - `PartModule.reflectedAttributeCache.Clear()` (line 101).
   - `Part.reflectedAttributeCache.Clear()` (Part.cs:655).
   - `BaseFieldList.reflectedAttributeCache.Clear()` (BaseFieldList.cs:71).
   - `BaseActionList.reflectedAttributeCache.Clear()` (BaseActionList.cs:43).
4. `VesselModuleManager.modules.Clear(); VesselModuleManager.CompileModules();` — the private/static method will need to be called via reflection.
5. Re-run `KSPScenarioType.GetAllScenarioTypesInAssemblies()` and update whatever cache `ScenarioRunner` consults.
6. Rebuild part prefabs: for each affected `AvailablePart`, re-resolve each module by name and `AddComponent` the new Type onto the prefab `Part`. Re-run `partPrefab.partInfo` wiring if needed. This is the riskiest step — prefab state setup logic lives in `PartLoader.CompileParts` and is not trivially re-runnable on an existing prefab without also re-running `OnLoad` at the prefab level. Option: snapshot the prefab's `ConfigNode` at original load time, and on reload, run `AddModule(configNode)` against the prefab for each affected module — this replays the exact original init path.

#### Phase 4 — Reinstantiate

1. Scenario modules: for each snapshot, call `ScenarioRunner.Instance.AddModule(node)` — this resolves the new Type by name and `Load`s state.
2. Vessel modules: `VesselModuleManager.AddModulesToVessel` is designed for fresh vessels. For existing vessels, reimplement the same loop but skip types that weren't reloaded. Then call `Vessel.LoadVesselModules` with the snapshot ConfigNode.
3. Part modules: for each snapshot, find the target part by persistent ID, call `part.AddModule(moduleName)` (Part.cs:5787) — this resolves the new Type and `AddComponent`s. Then call `module.Load(snapshotNode)`. Preserve module order: `AddModule` appends; if order matters (it does for some mods that iterate `part.Modules` expecting a specific order), you need an ordered rebuild path.
4. `[KSPAddon]` re-instantiation: scan the new assembly for `[KSPAddon]` matching the current `HighLogic.LoadedScene`, `AddComponent` onto fresh GameObjects as `AddonLoader.StartAddon` does. Update `loadedOnceList` accordingly.
5. Arbitrary MonoBehaviours: only feasible for types the user explicitly opts in via `IHotReloadable` or a registration API. We can't generically enumerate "all MonoBehaviours from mod X in the scene" cheaply; a registry kept by a base class or an attribute is the escape hatch.

#### Phase 5 — Rewire and thaw

1. Let Harmony repatch: the reloaded addon's new `Awake`/`Start` will re-apply patches.
2. Call `OnStart` on every reinstantiated `PartModule` (mirroring how `Part` drives module start on vessel load). `PartModule.OnStart` is public; just invoke with the saved `PartModule.StartState` — you'll need to pick a reasonable state (e.g., `StartState.Flying` mid-flight).
3. Re-open PAWs that were open before phase 1.
4. Restore input-lock set, or clear and re-acquire.
5. Re-fire `GameEvents.onVesselWasModified` for each touched vessel so other subscribers refresh.
6. Vessels back off-rails (`vessel.GoOffRails()` for the ones that were off-rails before).
7. Restore `Time.timeScale`.

### 6.3 What can still go wrong

- **PartModule ordering contract.** Some mods assume "ModuleX is at index N" or iterate `part.Modules` stopping at the first of a type. Appending via `AddModule` in a different order changes behavior. Mitigation: after reinstantiation, reorder `part.Modules` to match the pre-snapshot order by name.
- **Active coroutines assuming state.** A newly-`OnStart`-ed module may assume "my animation coroutine is still running" because the snapshot says so. There's no good generic fix; the mod must be reload-aware (e.g., an `OnAfterReload` hook where it re-kicks coroutines from the restored state).
- **Joint rebuilds.** If a reloaded `PartModule` implements `IActiveJointHost`, destroying it mid-flight could invalidate joints. Detect via interface, quiesce joints first (go on-rails covers this), then rebuild.
- **Resource flow desync.** `ResourceFlowGraph` / `PartSet` caches per-vessel module references. Rebuild via `vessel.resourcePartSet.RebuildInPlace()` post-swap.
- **FlightIntegrator interfaces.** `IAnalyticPreview`, `IAnalyticTemperatureModifier`, `IAnalyticOverheatModule` — the integrator keeps a list of parts/modules implementing these. After swap, force a rebuild of its caches (look for a `Rebuild` or force via `vessel.SpawnCrew()`-adjacent paths; cheapest is `vessel.GoOffRails()` which re-enumerates).
- **Mod reloaded but its Harmony patches target a *different* reloaded mod.** Patch ordering within the reload set matters. Unpatch all doomed assemblies first, swap all, then re-`Start` all — never interleave.
- **Partial reload across a dependency edge left unreloaded.** Guard with an explicit check: if A is being reloaded and B depends on A but is not in the reload set, refuse the reload and tell the user to include B.

### 6.4 Required mod-side cooperation (recommended API surface)

Generic hot-reload cannot preserve non-`[KSPField]` state. Offer an opt-in API so mods can participate:

```csharp
public interface IHotReloadable
{
    void OnBeforeReload(ConfigNode node);   // write durable state
    void OnAfterReload(ConfigNode node);    // restore, re-kick coroutines, re-subscribe
}

[AttributeUsage(AttributeTargets.Class)]
public class HotReloadableAttribute : Attribute
{
    public bool PreserveOrder { get; set; } = true;
    public bool RebuildOnly   { get; set; } = false; // don't snapshot; just re-init
}
```

A mod that implements `IHotReloadable` gets snapshot fidelity beyond `KSPField`. A mod marked `RebuildOnly` signals "don't try to preserve state — just re-instantiate me cleanly." A mod that does neither gets best-effort `KSPField`-only preservation.

---

## 7. Suggested staging

1. **Phase 1 implementation — scene-boundary only.** Scope: `ScenarioModule` + `[KSPAddon]` + inactive vessels. No mid-flight, no prefab rebuild beyond a conservative "do any active vessel's `Part` prefabs contain a reloaded module? If yes, refuse reload and ask to return to KSC." High-value for mod dev loop; low risk.
2. **Phase 2 — add part prefab rebuild.** Unlocks reloading part-defining mods from the space center without a full game restart.
3. **Phase 3 — mid-flight via the five-phase swap.** Gated behind `IHotReloadable` opt-in for non-trivial mods; best-effort otherwise.
4. **Phase 4 — dependency-set reloads.** Detect dependency edges, compute transitive closure, reload as a unit.

Each phase is shippable on its own.

---

## 8. Things to prototype first (ordered by risk)

1. **Cache invalidation in isolation.** Write a test mod that loads a trivial `PartModule`, clears all seven caches listed in §3, loads a second copy of the same DLL under a renamed assembly, and verifies `GetClassByName` returns the new Type. This validates the invalidation list without any swap logic.
2. **Harmony unpatch round-trip.** Patch a method, unpatch by harmony ID, patch again from a reloaded assembly, verify the new patch runs. Catches ordering bugs cheaply.
3. **`GameEvents` delegate-sweep by source assembly.** This is the linchpin of mid-flight reload and the most likely place to find an undocumented internal layout change. Verify against the KSP version in `deps/`.
4. **Prefab `PartModule` component replacement.** Build a debug scene that instantiates an `AvailablePart` prefab, replaces a module component, and verifies the next `Instantiate` produces the new Type.
5. **Five-phase mid-flight swap on a toy module.** A `PartModule` with one `KSPField` and one coroutine, on a one-part vessel. If this works, scale up.
