# QuickMeds v1.2.0 — source verification

This package was statically checked before delivery. Runtime/API verification still comes from compiling it against the user's own SPT 4.1.6 installation.

## Preserved gameplay invariants

- No generic `ActiveHealthController.AddEffect<T>` patch.
- Adaptive timing starts at the owned `MedEffect.Started` state through `AddWorkTime(..., true)`.
- Movement retarget preserves effect progress and updates timing + animator together.
- Local/Fika ownership checks remain in place.
- ContinuousHealing effect-boundary reset logic remains in place.
- HealingAutoCancel detection still disables only QuickMeds' internal auto-cancel watcher.
- Pills/injectors remain excluded from QuickMeds' body-part-full auto-cancel heuristic.
- Food/drink remain excluded.

## Cleanup invariants

- Runtime install ZIP staging copies only `QuickMeds.dll`.
- MIT `LICENSE` is embedded in the assembly project and stays readable in the source package.
- Category/base `NodeType.Node` medicine templates are excluded from F12 catalog binding.
- Medicine group classification is cached per template ID.
- No old hidden debug migration config is bound.
- Dead `HealSpeed.Active`, `CurrentEffect`, manual `Clear`, and `ModEnvironment.IsInRaid` members are absent.
- Only ConfigurationManager metadata fields actually used by QuickMeds remain in the local attribute shim.

## v1.2.0 checks

- Runtime medicine signature reads `Prefab` and `UsePrefab` with null propagation; one sparse template can no longer abort the entire F12 catalog.
- `LogMedicineClassification` is null-safe for `UsePrefab`.
- Medicine category is bound before Debug; Debug uses a fresh OFF-by-default persisted key with visible name `Debug logging`.
- MedKit/Medical/other individual-item binding path remains present after grouped pills/injectors.