# QuickMeds — SPT 4.1.3

Healing that doesn't root you in place.

- **Move while healing** — sprint with a medkit out, walk while splinting, with separate on/off switches for each
- **Healing speed follows your movement** — faster standing still, slower running
- **Heal auto cancel** — stops the moment the body part is full instead of finishing the animation for nothing

**Inspired by [SmartAction](https://github.com/yox92/SmartAction) by yox92**, and the auto-cancel idea comes from [Healing Auto Cancel](https://github.com/minihazel/HealingAutoCancel) by minihazel. This is an independent implementation for SPT 4.1.3, not a port of either.

## Installation

Extract into your **SPT root folder** — the one containing `BepInEx\` and `EscapeFromTarkov_Data\`. Files land in `BepInEx\plugins\QuickMeds\`.

**Requires SPT 4.1.3.** Client-only, no server mod.

## Settings

All in the **F12** menu.

### 1. Movement

| Setting | What it does |
| --- | --- |
| Sprint while using meds | Run while using bandages, medkits, pills |
| Walk while using splints or surgery kits | Move at all while splinting or doing surgery |
| Sprint while using splints or surgery kits | Run while splinting or doing surgery. Needs the walk setting on. |

Splints and surgery kits are separated from ordinary meds so you can allow running with a bandage but not while splinting a leg.

### 2. Healing

| Setting | What it does |
| --- | --- |
| Heal auto cancel | Stops as soon as the body part is full, or the item runs out |

A part that is still bleeding keeps healing — otherwise the bleed would undo the work the moment the item was put away. Broken bones always take the full time.

### 3. Healing speed

| Setting | Default | What it does |
| --- | --- | --- |
| Adaptive healing speed | on | Master switch for the three speeds below |
| Speed - standing still | 1.3 | Faster when not moving |
| Speed - walking | 1.0 | Normal speed |
| Speed - sprinting | 0.8 | Slower if you run through it |
| Log details to console | off | Diagnostics, for reporting a problem |

Set all three speeds to 1.00 to keep normal healing times while still using the movement settings.

The sliders stop at 0.70 and 2.50 deliberately. Past that the animation and the healing timer visibly stop lining up, because the game treats them as two separate systems.

**Which items are affected:**

| | Speed | Animation |
| --- | --- | --- |
| Bandages, medkits, splints, surgery kits | yes | yes |
| Pills, injectors | no | no |
| Food and drink | no | no |

Pills and injectors are left alone because their timer is how long the *buff* lasts, not how long the injection takes — speeding it up would shorten your painkillers. Food and drink are untouched entirely.

## Compatibility

**Make Meds Great Again** provides the same movement features. If it is installed, QuickMeds detects it and leaves movement to it automatically. The healing features carry on working.

**Healing Auto Cancel** — same arrangement. If it is installed, QuickMeds leaves auto-cancel to it.

**Modded and backported meds** work with no changes. Item types are identified by their component data, using the same test the game uses on itself, so a backported splint is recognised exactly like a vanilla one. No list of item IDs to maintain.

**Fika:** every patch acts only on your own player, and the mod disables itself on a headless client. **Not yet tested in a live co-op raid** — the design is right, but I would rather say so than claim compatibility I have not confirmed.

## Known limitations

**You can sprint briefly between body parts.** The game clears the "using meds" state in the gaps between limbs, so nothing blocks sprinting there. The only way to close it was to refuse that clear, which broke healing outright — the player reached for the item and got stuck, hands off screen, unable to act. Being able to sprint for a moment between limbs is a fair trade against that.

**A surgery kit used on an arm counts as a surgery item.** The movement settings check what is in your hands, not which body part you are treating. Keeps the whole thing stateless and reliable.

**Changing stance mid-limb adjusts the timing but not the animation.** The animation keeps the speed it was given when that body part started; the next one picks up the new speed. Changing animation speed partway through could restart the put-away clip.

## Uninstalling

Delete `BepInEx\plugins\QuickMeds\`. Nothing is written to your profile.

---


MIT.

---

If you'd like to support my work, you can [buy me a coffee](https://ko-fi.com/its_blackhawk) ☕
