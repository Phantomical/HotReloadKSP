# Changelog

## Unreleased
### Added
* Other assemblies can now declare a `static void OnHotReload(Assembly old,
  Assembly new)` method that gets invoked on every hot-reload of a *different*
  assembly. The set of declaring types is cached per-assembly so the broadcast
  doesn't rescan every type on every reload.

### Fixed
* Sibling `PartModule` references on the same part (e.g. stock
  `ModuleAnimationGroup._Modules` populated via `FindModulesImplementing`)
  are now repointed at the new instance after a hot-reload swap, so
  cross-module behaviour like the Drill-O-Matic spin animation tracks the
  live module instead of the orphaned old one.

## v0.1.4
### Fixed
* Avoid copying certain `PartModule` fields that are actually supposed to be
  initialized by the part module itself.

## v0.1.3
### Added
* MonoBehaviours containing `T[]` or `List<T>` where `T` is a type in the assembly
  being reloaded are now handled correctly.
* static `OnHotLoad`/`OnHotUnload` callbacks can now optionally take an `Assembly`
  parameter to the old/new assembly, respectively.
* Custom settings nodes are now hot-reloaded.

### Changed
* PartModule reloads are now much closer to how KSP works normally.
* VesselModule reloads are now also much closer to how KSP works normally, `OnLoadVessel`
  will also be called for newly reloaded modules on loaded vessels.
* The order that callbacks are made and reloads are done has been reordered to
  make more sense. `OnHotLoad` callbacks have now been moved to the front, so
  static initialization is complete before anything else is loaded in.

## v0.1.2
### Fixed
* Clean up dependencies in the internal CKAN.

## v0.1.1
### Fixed
* Exclude `HotReloadKSP.Test` from the release zip.

## v0.1.0
This is the very first release of HotReloadKSP.
