# QuickMeds v1.2.0 — regression checklist

Enable `F12 > QuickMeds > 5. Debug > Debug logging` for the first test.

## Build/package

- Build must report zero compiler errors.
- The install ZIP must contain exactly one runtime file: `BepInEx/plugins/QuickMeds/QuickMeds.dll`.
- `dist/BUILD-INFO.txt` must exist beside the ZIP, not inside it.
- `dist/QuickMeds.dll` must exist as a standalone copy.

## Startup

- Raid must start normally.
- Startup must report `capture=True started=True retarget=True teardown=True adaptiveAvailable=True`.
- Log should report `medicine F12 list ready` after ItemFactory initializes.
- There must be no QuickMeds warning, exception, or safety lock.

## F12 cleanup

- No fake category rows such as `Medical - Medical` or `MedKit - MedKit`.
- Individual medicine entries should use readable/localized names where EFT provides them.
- **All pills** defaults OFF.
- **All injectors** defaults OFF.
- Morphine follows **All injectors**, not **All pills**.
- Golden Star/Vaseline-style balms are not controlled by **All pills**; they stay individual.
- Turn **Walk while using splints or surgery kits** OFF: the sprint row must immediately become OFF, grey, and unclickable.

## Dynamic pills/injectors

Enable the category being tested, use strong speed differences such as still `0.25`, walking `1.0`, sprinting `2.5`, and change movement during the same use.

Expected:
- `RESCALE Started` changes remaining work time;
- `ANIM write` / animator restore changes animation to the same target;
- `RETARGET applied` confirms the transition.

Disable the category and repeat: the item should remain vanilla.

## Existing medicine regression

Test medkit, bandage/bleed treatment, normal splint, Aluminum splint, CMS/Surv12 and Grizzly. Verify no stuck hands, no double put-away, correct weapon draw, and dynamic movement retarget.

Aluminum splint remains a surgery/splint movement item; Grizzly remains normal medkit behavior.

## Auto-cancel

- partially damaged limb reaches full HP -> stops;
- still bleeding -> does not stop prematurely;
- fracture -> full treatment continues;
- pills/injectors do not use QuickMeds stop-when-full auto-cancel logic.

## ContinuousHealing / HealingAutoCancel

Compatibility logic is unchanged from v1.1.5+. When installed, verify the existing `COMPAT ... detected` startup logs and chained-effect boundary/reset behavior.

## v1.2.0 F12 regression

1. Start to the main menu and wait for the medicine catalog to bind.
2. Open/reopen F12. Confirm section order is Movement, Healing, Healing speed, Per-medicine QuickMeds, Debug.
3. Confirm Debug logging starts Disabled on a config that previously had the old debug key Enabled.
4. Confirm All pills and All injectors are followed by individual heal entries such as AFAK/IFAK/Grizzly/Salewa/CAR where those templates exist.
5. Confirm there is no `QuickMeds: could not build per-medicine F12 settings` error.
