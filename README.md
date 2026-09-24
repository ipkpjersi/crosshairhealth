# CrosshairHealth

Draws GTA IV's target-health ring back while C06alt's First Person Mod is providing the
view.

## The problem

Vanilla GTA IV has a complex reticule: a centre dot, a circle, and a ring that colours by
the health of whoever you are aiming at. FusionFix's `AlwaysDisplayHealthOnReticle` keeps
that on for keyboard and mouse players.

C06alt's First Person Mod does not use that reticule. It pulls the `hud_crosshair` texture
with `GET_TEXTURE_FROM_STREAMED_TXD` and composites its own dot and ring with `DRAW_SPRITE`
and `DRAW_RECT`, so the complex reticule, and the health ring with it, is never drawn in
first person. Setting the mod's crosshair alpha to 0 leaves no crosshair at all rather than
revealing the game's, which is how that was established.

ZMenu IV's first person keeps the real reticule, so the loss is specific to C06alt.

## What this does

A ScriptHookDotNet script that draws an arc between C06alt's dot and its circle, filled
clockwise in proportion to the target's health and coloured from GTA IV's own HUD green to a
matching muted red.

Two gates, both strict:

- **C06alt has to be drawing.** Its state is read from the mod itself, not inferred, so
  nothing is drawn in third person, under another first-person mod, or with C06alt absent.
- **Somebody has to be under the crosshair.** No target, no ring.

Health is not drawn in a vehicle, which matches the game: GTA IV draws a crosshair in a car
and never puts target health on it, in third person or under ZMenu IV's first person alike.

## Installing

Needs an installed C06alt First Person Mod, FusionFix or another ASI loader, and
ScriptHookDotNet, which LCPDFR bundles.

    cp CrosshairHealth.net.dll  <game>/scripts/
    cp CrosshairHealth.ini      <game>/

## Configuring

`CrosshairHealth.ini` sits beside the game executable. Every value is optional. Sizes and
offsets are pixels at 1440p and scale with screen height.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | 1 | 0 draws nothing |
| `Radius` | 17 | ring size; C06alt's own circle is about 21 |
| `Thickness` | 5 | how thick the ring's line is |
| `OffsetX` / `OffsetY` | 0 / -28 | where C06alt puts its crosshair, which is not screen centre |
| `ToggleKey` | 117 (F6) | flips it on and off in play; 0 disables the key |
| `Debug` | 0 | draws unconditionally and logs once a second |
| `Probe` | 0 | walks the aimed-at ped's structure, for finding offsets |
| `MaxHealthOffset` | 0x110 | where a ped keeps its maximum health |
| `FpObjectRva` | 0x5d9d0 | C06alt's state object, from its module base |
| `FlagOnFootOffset` | 0x155 | first person, one mode |
| `FlagInVehicleOffset` | 0x15a | first person, other mode |
| `FlagSuppressedOffset` | 0x112 | crosshair suppressed |

The five offsets are hex, with or without an `0x` prefix, and a bad value is ignored rather
than applied. They only need touching on a build other than the one they were found on, so
see below.

## What it reads, and how those offsets were found

Two pieces of memory are read directly, both found by observation rather than guesswork.

**C06alt's first-person state.** `FirstPerson.asi` v1.3 (420352 bytes) keeps a static object
at RVA `0x5d9d0`. The caller at `0x100035bf` gates the crosshair draw on three of its bytes,
and this reads the same three:

    +0x155   first person, one mode
    +0x15a   first person, other mode
    +0x112   suppressed

Drawn when `(+0x155 or +0x15a) and not +0x112`, which is C06alt's own condition. The object
base was placed by noting that the draw routine at `0x10005b20` tests `0x150(%esi)` while an
absolute store writes `0x1005db20`, and `0x1005d9d0 + 0x150` is exactly that address.

**Maximum health**, a float at ped `+0x110`. The API has no getter for it: GTA IV exposes
`SET_CHAR_MAX_HEALTH` and a `MaxHealth` setter and nothing to read either back. Probing peds
while shooting them settled it, with `+0x110` holding 100 while the reported health fell
through 78, 51 and 23. Current health is not stored as a plain int or float anywhere in the
first 0x2000 bytes, which does not matter, because `ped.Health` reports it.

Both are checked before use, and all five offsets live in the ini so another build can be
accommodated without recompiling.

**On another game version, Complete Edition being the obvious one:** the three C06alt
offsets are relative to `FirstPerson.asi`'s own module base rather than to the game, so they
carry over if the same v1.3 binary is used there. `MaxHealthOffset` is a `CPed` field and
belongs to the game version, so expect it to move on 1.2.0.59. Set `Probe = 1`, aim at
someone, shoot them, and read `crosshairhealth.log` for the offset that holds steady while
the reported health falls; then put that number in the ini.

Worth knowing: the sanity check on maximum health only rejects values outside 1 to 10000, so
a wrong offset that happens to hold a plausible float will be accepted and scale the ring
against nonsense. Probing is the way to be sure rather than assuming the default carried.

## Building

Needs `mono-devel` for `mcs`, and an installed LCPDFR for the ScriptHookDotNet reference
assembly.

    ./build.sh                 # finds GTA IV through Steam's library list
    ./build.sh /path/to/GTAIV  # or point it at the game yourself

## Notes for anyone doing something similar

- **`GTA.Graphics` draws in pixels, not in 0 to 1 screen space.** Drawing in fractions puts
  things in the top-left corner as a speck. LCPDFR's own `Mouse.cs` passes pixel
  coordinates; its `Gui.cs` only normalises when calling the raw native.
- **Do not hand a stale ped to a native.** Iterating whatever `World.GetPeds` returns and
  calling one on each eventually access-violates, and an access violation is a corrupted
  state exception that an ordinary `catch` does not stop, so the script is removed
  mid-session. Peds are validated and isolated here, and the drawing handler is marked to
  catch those exceptions.
- **Bound any memory walk with `VirtualQuery`.** Reading past the end of an allocation
  faults for the same reason.

## Scope

GTA IV 1.0.7.0 under Proton, with C06alt First Person v1.3 and LCPDFR 1.1. Additive and
reversible: delete the two files and it is gone.
