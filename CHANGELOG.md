# Changelog

## v1.2.0 — 2026-10-11

Stable release promoted from the v1.1.x test series. This release combines the adaptive healing/animation rewrite, live movement retargeting, per-medicine controls, compatibility work, catalog fixes, and clean packaging validated during testing.

### Final release fixes

- **Per-medicine catalog crash fixed:** some valid/custom item templates have a null `Prefab` or `UsePrefab`. The v1.1.7 classifier dereferenced those fields and aborted the whole catalog, which is why F12 showed only `All pills` and `All injectors`. Resource paths and debug classification are now null-safe.
- **Individual healing items are restored:** AFAK, IFAK, Grizzly, Salewa, CAR, splints, surgery kits, balms/ointments, and compatible custom medicine bind as individual F12 toggles after ItemFactory is ready. Pills and injectors remain grouped.
- **Debug logging is OFF by default for existing test configs too:** the release retains the fresh persisted debug key introduced in v1.1.9 while keeping the visible label `Debug logging`, preventing an old enabled test value from carrying forward.
- **Debug is always the final section:** the medicine master settings are bound before the Debug category so ConfigurationManager renders `4. Per-medicine QuickMeds` before `5. Debug`.
- The clean runtime packaging and source cleanup from the test series are retained.

## Clean runtime package

- The install ZIP now contains only `BepInEx/plugins/QuickMeds/QuickMeds.dll`.
- `README.md`, `TESTING.md`, and `BUILD-INFO.txt` are no longer copied into the runtime plugin folder.
- `BUILD-INFO.txt` is still generated beside the install ZIP for bug reports.
- A standalone `dist/QuickMeds.dll` is also produced.
- Temporary staging plus `bin/obj` build folders are removed after a successful build.
- The MIT license text is embedded into `QuickMeds.dll`; the readable `LICENSE` file remains in the source package.

## F12/catalog cleanup

- Category/base templates such as the abstract `Medical` and `MedKit` nodes are filtered out and no longer create fake F12 rows.
- Individual medicine rows now prefer EFT's localized display name instead of raw internal names such as `item_meds_*` when localization is available.
- Morphine/injector/pill/balm grouping from v1.1.7 is retained.
- Medicine use-form classification is cached per template ID so movement retargets do not rebuild strings repeatedly.
- The dependent surgery/splint sprint row hides its reset button so the disabled/grey row cannot be indirectly toggled while walking is OFF.

## Source cleanup

Removed code that was no longer used:

- `HealSpeed.Active`
- `HealSpeed.CurrentEffect`
- public manual `HealSpeed.Clear()` wrapper
- unused raid helper in `ModEnvironment`
- unused ConfigurationManager attribute fields
- old hidden v1.1.6 debug-migration binding
- redundant per-template `DefaultEnabled` state (all individual medicine rows default ON)
- duplicate `_controller` reflection field in the heal-session patch
- unused reason string on the adaptive safety-lock method

Plugin-detection token normalization is also done once per scan instead of once per plugin candidate.
