# BlackHawk QuickMeds — SPT 4.1.6 v1.2.0

Source package for SPT 4.1.6. `build.ps1` compiles against the DLLs from your own SPT installation and does not install or modify SPT.

## Main features

- Sprint while using normal medicine, with separate walk/sprint controls for splints and surgery.
- Heal auto-cancel for heal/repair items.
- Movement-dependent medicine timing + animation, 0.25x–2.50x.
- Dynamic timing/animation retarget while medicine is already being used.
- Pill and injector support with grouped category switches.
- Food/drink are never modified.
- ContinuousHealing and HealingAutoCancel compatibility retained.

## F12 layout

### 1. Movement

- Sprint while using meds
- Walk while using splints or surgery kits
- Sprint while using splints or surgery kits

When surgery/splint walking is OFF, the sprint option is forced OFF and its custom F12 control is greyed out/unusable.

### 2. Healing

- Heal auto cancel

### 3. Healing speed

- Adaptive healing speed
- Speed - standing still
- Speed - walking
- Speed - sprinting

### 4. Per-medicine QuickMeds

- **All pills** — pill/tablet/capsule medicine. Default OFF.
- **All injectors** — stimulators/injectors, including Morphine. Default OFF.
- Medkits and other heal items remain individually selectable — for example AFAK, IFAK, Grizzly, Salewa, CAR, bandages, splints and surgery kits. Balms/ointments and ambiguous/custom medicine also remain individual rather than being guessed into pills/injectors.

QuickMeds does not assume `Drugs == pills`: Morphine is a vanilla `Drugs` template but is an injector, while Golden Star/Vaseline-style drugs are balms. Unknown custom drug templates stay individual instead of being guessed.

The runtime catalog ignores EFT category/base nodes and prefers localized item names for the remaining individual entries.

### 5. Debug

- Debug logging — detailed diagnostics for bug reports. Default OFF.

Debug is bound after the medicine section so it remains the final QuickMeds category in F12.

## Runtime package

After a successful build, the install ZIP contains only:

```text
BepInEx/
└─ plugins/
   └─ QuickMeds/
      └─ QuickMeds.dll
```

`README.md`, `TESTING.md`, and build diagnostics stay outside the runtime ZIP. The MIT notice is embedded in the DLL and the readable `LICENSE` remains in this source package.

## Timing design

QuickMeds does not patch EFT's generic health-effect factory. The initial MedEffect `Added` / start-delay stays vanilla. When the exact owned MedEffect reaches `Started`, QuickMeds adjusts the running work duration through EFT's `AddWorkTime(..., reset: true)` path and sets the medicine animator to the same multiplier. Movement/F12 changes during `Started` preserve progress and retarget both together.

## Compatibility

- Make Meds Great Again: QuickMeds leaves movement overrides to MMGA when detected.
- HealingAutoCancel: QuickMeds disables only its internal auto-cancel watcher when the external mod is detected.
- ContinuousHealing 1.6.3: every chained MedEffect is treated as a fresh vanilla-timed effect before QuickMeds applies the current movement target.
- Fika: local ownership and network-aware timing guards are retained; live co-op verification is still recommended.

## Build

Run:

```powershell
.\build.ps1 -SptPath "C:\EFT .0.16.9.5.40743 - SPT 4.1"
```

Successful output is kept under `dist`:

```text
dist\QuickMeds.dll
dist\BlackHawk-QuickMeds-SPT-4.1.6-v1.2.0.zip
dist\BUILD-INFO.txt
```

Only the install ZIP contents belong in SPT. `BUILD-INFO.txt` is for bug reports.

Do not install a build if the compiler reports any error.

MIT licensed; see `LICENSE`.

## v1.2.0 catalog note

The v1.1.7 log exposed a null-resource-key bug in the runtime catalog builder. If an EFT/custom medicine template omitted `Prefab` or `UsePrefab`, catalog binding stopped before individual entries were created. v1.2.0 makes those fields null-safe, so grouped pills/injectors and individual medkits/medical items can coexist in F12.
