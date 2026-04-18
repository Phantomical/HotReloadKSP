# HotReload

This is a mod for KSP that allows you to hot-reload DLLs while the game is
running. Use it to quickly iterate on your mod during development without
needing to restart the game every time you want to make a change.

HotReload will automatically reload any of the following:
* Part Modules
* Vessel Modules
* Scenario Modules
* MonoBehaviour Components

Anything beyond that will not be reloaded and will need to be implemented
manually by the mod being reloaded.

## Making your mod hot-reloadable
To avoid breaking mods and to keep the menu manageable hot-reloading is opt-in.
You can opt-in by adding a custom `AssemblyMetadata` attribute to your assembly,
like this:
```cs
using System.Reflection;

[assembly: AssemblyMetadata("HotReload", "true")]
```

## What gets transferred over when hot-reloading?
How a reload works depends on its type. Stuff that can use KSP's save/load
system should be much more reliable, the way that MonoBehaviours are reloaded
will likely lead to some jank.

### Vessel and Scenario modules
How these work is pretty straightforward:
* the old version of the module gets saved to a `ConfigNode`,
* the old version of the module is then destroyed,
* the new version of the module is then added back to the game object,
* the new version reloads its state from the config node in step 1.

This should work pretty much exactly like a scene switch. I expect most vessel
and scenario modules to be compatible.

### Part modules
Part modules are more complicated. KSP compiles part prefabs at load time and
then new parts are instantiated from that. HotReload attempts to mimic that.

The workflow for part modules is:
* All live part modules from the old assembly are saved and destroyed.
* All part module prefabs from the old assembly are destroyed.
* New part module prefabs are then created for the new assembly.
* The new part modules are then instantiated from the prefab onto the part,
  loaded, and injected back to their original location in the module list.

### MonoBehaviors
KSPAddons and other UI MonoBehavior components are trickier to make work.
There's no way to serialize their state in a way that will work most components.
This means that hot-reload support for them is a bit more limited.

Here's how hot-reloading a MonoBehavior works:
* The game object containing the MonoBehavior is first disabled.
* Corresponding MonoBehaviors from the new assembly are copied over.
* Fields on the old object are copied over to the new one as long as they do
  have types from the assembly being reloaded. This means that a field of type
  `T` will be left as default if `T` is from the assembly being reloaded. The
  same applies to `List<T>`.
* Any references to other components that are being reloaded are updated to
  instead point at the new component being loaded in.
* `OnHotLoad` is called on the new component, if it has a method with this
  signature:
  ```cs
  void OnHotLoad(MonoBehavior old);
  ```
* `OnHotUnload` is then called on the old component, if it has a method with
  this signature:
  ```cs
  void OnHotUnload(MonoBehavior new);
  ```
* The old component is then re-enabled and destroyed.
* The new component is now re-enabled.

In short:
* All fields with types defined in other assemblies are copied over by default.
* Any references to other MonoBehaviors defined in the same assembly (and so are
  also being hot-reloaded) will be automatically updated to point at the new
  version.
* All other fields will be left as their default value.
* You can implement `OnHotLoad` or `OnHotUnload` to manually copy state over if
  you have more complicated needs.

## Life cycle events
You can define any of the following static methods:
```cs
// Called on types in the new assembly being loaded in.
static void OnHotLoad(Assembly old, Assembly new);

// Called on types in the old assembly that is being replaced.
static void OnHotUnload(Assembly old, Assembly new);
```

They will be called in the middle of MonoBehavior reload.

If you need to do things when _other_ assemblies are reloaded then you can add
a listener to `HotReload.OnAssemblyHotReload` and then drive whatever you want
to reload in your own codebase.

## License
HotReloadKSP is available under the MIT license.
