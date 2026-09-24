// Restores GTA IV's target-health ring while C06alt's First Person Mod is drawing.
//
// Vanilla GTA IV has a complex reticule: a centre dot, a circle, and a ring that colours
// by the health of whoever you are aiming at. FusionFix's AlwaysDisplayHealthOnReticle
// keeps that ring on for keyboard and mouse players.
//
// C06alt's mod does not use the game's reticule at all. It pulls the hud_crosshair texture
// with GET_TEXTURE_FROM_STREAMED_TXD and composites its own dot and ring with DRAW_SPRITE
// and DRAW_RECT, so the complex reticule, and the health ring with it, is never drawn in
// first person. Setting the mod's crosshair alpha to 0 leaves no crosshair at all rather
// than revealing the game's, which is how that was established. ZMenu IV's first person
// keeps the complex reticule, so the health only goes missing with C06alt.
//
// This draws that ring back: an arc between the mod's dot and its outer circle, filled
// clockwise in proportion to the target's health and coloured green through red. It draws
// nothing at all unless the player is in first person and actually aiming at someone, so
// it costs nothing the rest of the time and never covers the normal third-person reticule,
// which still has FusionFix's ring of its own.
//
// Additive and reversible: delete the file to be rid of it. See TODO.md item 13.

using System;
using System.Drawing;
using GTA;

public class CrosshairHealth : Script
{
    // Radius of the ring as a fraction of screen height. Measured from a screenshot at
    // 2560x1440: C06alt's circle has a radius of about 21px, so 14px sits inside it with
    // clear air on both sides, which is where the game's own health ring lives.
    private static float RingRadius = 14f / 1440f;

    // Thickness of the arc, again relative to screen height. About 4px at 1440p.
    private static float RingThickness = 4f / 1440f;

    // Where C06alt draws its crosshair, relative to the middle of the screen. It is not
    // centred: measured 16px above centre at 1440p, and the ring drawn at true centre came
    // out about 28px below it. Fractions of screen height so this holds at any resolution.
    private static float OffsetX = 0f;
    private static float OffsetY = -28f / 1440f;

    // Off means draw nothing at all. C06alt replaces the game's reticule with one of its
    // own, which is why the health ring has to be redrawn here; ZMenu IV's first person
    // keeps the real reticule, so with ZMenu this should be turned off and the game left
    // to draw its own.
    private static bool Enabled = true;

    // Key that flips the ring on and off in play, so switching between C06alt's first
    // person and ZMenu IV's does not mean alt-tabbing to edit the ini. 117 is F6, which
    // nothing else in this stack binds. Set ToggleKey to 0 in the ini to disable it.
    private static int ToggleKey = 117;

    // Probe mode. Walks the aimed-at ped's structure looking for fields that match the
    // health the game reports, and logs them with their neighbours. Used to find where
    // CPed keeps current and maximum health, since the API exposes no getter for the
    // maximum. Set Probe = 1 in the ini, aim at someone, shoot them once, and compare.
    private static bool Probe = false;

    // How far into the ped structure to look, in bytes.
    private const int ProbeBytes = 0x2000;

    // Reading C06alt's own state, so the ring appears exactly when its crosshair does.
    //
    // FirstPerson.asi keeps a static object at RVA 0x5d9d0. Its draw routine is gated on
    // three bytes of that object, and this reads the same three: the crosshair is drawn
    // when either first-person flag is set and the suppression byte is clear.
    //
    // Found by disassembling v1.3 (420352 bytes): the caller at 0x100035bf tests +0x15a,
    // +0x155 and +0x112 before calling the draw routine at 0x10005b20, and the routine's
    // own +0x150 check corresponds to the absolute store at 0x1005db20, which is what puts
    // the object's base at 0x1005d9d0. Offsets therefore hold for this build only, which
    // is why a mismatch falls back to the camera test rather than failing outright.
    private const int FpObjectRva = 0x5d9d0;
    private const int FlagOnFootRva = FpObjectRva + 0x155;
    private const int FlagInVehicleRva = FpObjectRva + 0x15a;
    private const int FlagSuppressedRva = FpObjectRva + 0x112;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet =
        System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string moduleName);

    // Used to find how far the ped's allocation actually runs, so the probe stops at the
    // end of committed memory instead of walking off it. Reading past the end faults, and
    // an access violation is a corrupted-state exception rather than an ordinary one, so
    // it cannot simply be caught per read.
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int VirtualQuery(IntPtr address, out MemoryBasicInformation buffer,
                                           int length);

    private const uint MemCommit = 0x1000;
    private const uint PageReadable = 0x02 | 0x04 | 0x20 | 0x40; // R, RW, XR, XRW

    // How many bytes can safely be read starting at this address, capped at want.
    private static int ReadableBytes(int address, int want)
    {
        MemoryBasicInformation info;
        int size = System.Runtime.InteropServices.Marshal.SizeOf(
            typeof(MemoryBasicInformation));

        if (VirtualQuery((IntPtr)address, out info, size) == 0)
        {
            return 0;
        }
        if (info.State != MemCommit || (info.Protect & PageReadable) == 0)
        {
            return 0;
        }

        long end = info.BaseAddress.ToInt64() + info.RegionSize.ToInt64();
        long available = end - address;
        if (available < 0)
        {
            return 0;
        }

        return available < want ? (int)available : want;
    }

    private static IntPtr firstPersonModule = IntPtr.Zero;
    private static bool moduleChecked;
    private volatile bool toggleRequested;

    // GTA IV's own HUD health green, sampled from the radar arc: a muted sage, nothing
    // like a saturated green. The low-health end is a matching muted red rather than a
    // bright one, so the two ends belong to the same palette.
    private static readonly Color HealthyColor = Color.FromArgb(230, 72, 111, 73);
    private static readonly Color HurtColor = Color.FromArgb(230, 140, 62, 58);

    // How many straight segments make up a full circle. Enough that the arc reads as
    // curved at the sizes involved, few enough to stay cheap per frame.
    private const int Segments = 48;

    // Maximum health, read straight out of the ped.
    //
    // The API has no getter for it: GTA IV exposes SET_CHAR_MAX_HEALTH and a MaxHealth
    // setter, and nothing to read either back. So it comes from the ped structure, at a
    // fixed offset from the address the API does expose.
    //
    // Found by probing peds while shooting them, 2026-09-24: the float at +0x110 read 100
    // while the reported health fell through 78, 51 and 23, so it is the maximum and not
    // the current value. Current health is not a plain int or float anywhere in the first
    // 0x2000 bytes, which does not matter, because ped.Health reports it.
    private const int MaxHealthOffset = 0x110;

    // What that field is allowed to contain before it is treated as nonsense. A ped
    // reading zero, or something wild, falls back to the floor rather than producing a
    // ring that is empty or barely moves.
    private const float MaxHealthLowest = 1f;
    private const float MaxHealthHighest = 10000f;

    // Used only when the structure cannot be read at all.
    private const float FullHealthFloor = 100f;

    // How close the camera has to sit to the player to count as first person, in metres.
    // The third-person cameras sit several metres back on foot and further in a vehicle,
    // so this separates them comfortably without needing to know which mod owns the view.
    private const float FirstPersonDistance = 1.4f;

    // Peds further away than this are not considered as free-aim targets.
    private const float MaxTargetDistance = 45f;

    // Upper bound on how many peds the free-aim scan will look at in one frame.
    private const int MaxTargetsScanned = 24;


    // Diagnostics. When on, the ring is drawn unconditionally in the middle of the screen
    // and one line a second goes to crosshairhealth.log in the game folder, saying what the
    // camera test and the target search actually returned. Turn off once it works.
    private static bool Debug = false;

    private DateTime lastLog = DateTime.MinValue;
    private DateTime lastProbe = DateTime.MinValue;
    private string lastError = "none";

    public CrosshairHealth()
    {
        this.Interval = 0;
        LoadSettings();
        this.PerFrameDrawing += new GraphicsEventHandler(this.OnPerFrameDrawing);
        this.KeyDown += new GTA.KeyEventHandler(this.OnKeyDown);
    }

    // Read CrosshairHealth.ini beside the game executable, if it is there. Every value is
    // optional and anything missing keeps the default above, so the file only needs to
    // hold what is being changed.
    private static void LoadSettings()
    {
        try
        {
            if (!System.IO.File.Exists("CrosshairHealth.ini"))
            {
                return;
            }

            foreach (string raw in System.IO.File.ReadAllLines("CrosshairHealth.ini"))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("["))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split < 1)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                float number;
                bool parsed = float.TryParse(value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number);

                switch (key)
                {
                    case "Enabled":   if (parsed) { Enabled = number != 0f; } break;
                    case "Debug":     if (parsed) { Debug = number != 0f; } break;
                    case "Radius":    if (parsed) { RingRadius = number / 1440f; } break;
                    case "Thickness": if (parsed) { RingThickness = number / 1440f; } break;
                    case "OffsetX":   if (parsed) { OffsetX = number / 1440f; } break;
                    case "OffsetY":   if (parsed) { OffsetY = number / 1440f; } break;
                    case "ToggleKey": if (parsed) { ToggleKey = (int)number; } break;
                    case "Probe":     if (parsed) { Probe = number != 0f; } break;
                }
            }
        }
        catch
        {
            // A malformed file should not stop the script loading.
        }
    }

    private void Log(string line)
    {
        try
        {
            System.IO.File.AppendAllText("crosshairhealth.log",
                DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch
        {
        }
    }

    // Everything here is wrapped: an exception thrown during PerFrameDrawing gets the
    // script removed from ScriptHookDotNet's list for the rest of the session, which is
    // exactly the defect CursorFix exists to work around. This must never be that script.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    private void OnPerFrameDrawing(object sender, GraphicsEventArgs e)
    {
        try
        {
            this.HandleToggle();

            if (!Enabled)
            {
                return;
            }

            // Gate one: C06alt has to be the thing drawing. Its own state is read rather
            // than inferred, so ZMenu IV using the same field of view cannot confuse it,
            // and with the mod absent or unreadable nothing is drawn at all. The camera
            // test stays available but is no longer what decides this.
            bool? fromMod = C06altCrosshairActive();
            bool firstPerson = fromMod.HasValue && fromMod.Value;
            Ped target = this.GetAimedAtPed();

            if (Debug)
            {
                // Always draw something, so a ring that never appears tells us the drawing
                // itself is wrong rather than the conditions around it.
                this.DrawHealthRing(e.Graphics, 1f);

                if ((DateTime.Now - this.lastLog).TotalSeconds >= 1.0)
                {
                    this.lastLog = DateTime.Now;
                    // camDist and fov are logged together so the two first-person mods
                    // can be told apart by their fingerprints, if they differ at all.
                    this.Log(string.Format(
                        "active={0} flags={6} camDist={1:0.000} fov={2:0.0} target={3} health={4} err={5}",
                        firstPerson, this.CameraDistance(), this.CameraFov(),
                        target == null ? "none" : target.Model.ToString(),
                        target == null ? -1 : target.Health, this.lastError, FlagDump()));
                }
            }

            if (!firstPerson)
            {
                return;
            }

            if (Probe && target != null && target.Exists())
            {
                this.ProbePed(target);
            }

            // Gate two: somebody has to be under the crosshair. No target, no ring, so the
            // view stays clean when just walking or driving around.
            if (target == null || !target.Exists() || !target.isAlive)
            {
                return;
            }

            float fraction = target.Health / this.FullHealthFor(target);
            if (fraction > 1f)
            {
                fraction = 1f;
            }
            if (fraction < 0f)
            {
                fraction = 0f;
            }

            this.DrawHealthRing(e.Graphics, fraction);
        }
        catch (Exception ex)
        {
            // Deliberately silent in play. A missing crosshair ring is not worth losing the
            // script, but the reason is kept so the next log line can carry it.
            this.lastError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    // The base address of FirstPerson.asi, or zero when it is not loaded. The module has
    // relocations, so this cannot assume the preferred base of 0x10000000.
    private static IntPtr FirstPersonModule()
    {
        if (!moduleChecked)
        {
            moduleChecked = true;
            try
            {
                firstPersonModule = GetModuleHandleA("FirstPerson.asi");
            }
            catch
            {
                firstPersonModule = IntPtr.Zero;
            }
        }

        return firstPersonModule;
    }

    private static byte ReadFlag(IntPtr module, int rva)
    {
        return System.Runtime.InteropServices.Marshal.ReadByte(
            (IntPtr)(module.ToInt32() + rva));
    }

    // Whether C06alt is currently drawing its crosshair, which is the only situation this
    // script should be drawing in. Returns null when the mod is not loaded at all, leaving
    // the caller to fall back on the camera test.
    private static bool? C06altCrosshairActive()
    {
        IntPtr module = FirstPersonModule();
        if (module == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            bool onFoot = ReadFlag(module, FlagOnFootRva) != 0;
            bool inVehicle = ReadFlag(module, FlagInVehicleRva) != 0;
            bool suppressed = ReadFlag(module, FlagSuppressedRva) != 0;

            return (onFoot || inVehicle) && !suppressed;
        }
        catch
        {
            return null;
        }
    }

    // True when the active camera sits about where the player's head is. This deliberately
    // asks the camera rather than the mod: it works the same whether C06alt or ZMenu is
    // providing the view, and stays correct if neither is.
    private bool IsFirstPerson()
    {
        Ped player = Player.Character;
        if (player == null || !player.Exists())
        {
            return false;
        }

        Camera camera = Game.CurrentCamera;
        if (camera == null)
        {
            return false;
        }

        return camera.Position.DistanceTo(player.Position) < FirstPersonDistance;
    }

    private void OnKeyDown(object sender, GTA.KeyEventArgs e)
    {
        if (ToggleKey != 0 && (int)e.Key == ToggleKey)
        {
            this.toggleRequested = true;
        }
    }

    // Flip the ring when the key handler has asked for it.
    private void HandleToggle()
    {
        if (ToggleKey == 0)
        {
            return;
        }

        // Nothing to poll: the toggle arrives through Script.KeyDown instead, which is the
        // supported route. This stays as the single place the state changes.
        if (this.toggleRequested)
        {
            this.toggleRequested = false;
            Enabled = !Enabled;
            Game.DisplayText(Enabled ? "Crosshair health: on" : "Crosshair health: off", 1500);
        }
    }

    // The three bytes, for the log, so a wrong offset shows up as flags that never move.
    private static string FlagDump()
    {
        IntPtr module = FirstPersonModule();
        if (module == IntPtr.Zero)
        {
            return "no-module";
        }

        try
        {
            return string.Format("foot={0} veh={1} supp={2}",
                ReadFlag(module, FlagOnFootRva),
                ReadFlag(module, FlagInVehicleRva),
                ReadFlag(module, FlagSuppressedRva));
        }
        catch
        {
            return "unreadable";
        }
    }

    private float CameraFov()
    {
        try
        {
            return Game.CurrentCamera.FOV;
        }
        catch
        {
            return -1f;
        }
    }

    private float CameraDistance()
    {
        try
        {
            return Game.CurrentCamera.Position.DistanceTo(Player.Character.Position);
        }
        catch
        {
            return -1f;
        }
    }

    // Log every offset in the ped structure holding the reported health, as an int or as a
    // float, together with the two values either side of it. Current health moves when the
    // ped is shot; a maximum sitting next to it does not, which is what identifies it.
    private void ProbePed(Ped ped)
    {
        if ((DateTime.Now - this.lastProbe).TotalSeconds < 1.0)
        {
            return;
        }
        this.lastProbe = DateTime.Now;

        try
        {
            int baseAddress = ped.MemoryAddress;
            int health = ped.Health;
            System.Text.StringBuilder found = new System.Text.StringBuilder();

            // Never read beyond what the process has actually committed.
            int limit = ReadableBytes(baseAddress, ProbeBytes);

            for (int offset = 0; offset + 4 <= limit; offset += 4)
            {
                int asInt;
                float asFloat;
                try
                {
                    asInt = System.Runtime.InteropServices.Marshal.ReadInt32(
                        (IntPtr)(baseAddress + offset));
                    asFloat = BitConverter.ToSingle(BitConverter.GetBytes(asInt), 0);
                }
                catch
                {
                    continue;
                }

                bool intMatch = asInt == health;
                bool floatMatch = asFloat > health - 0.5f && asFloat < health + 0.5f;
                if (!intMatch && !floatMatch)
                {
                    continue;
                }

                // The neighbours matter more than the hit itself: a maximum tends to sit
                // immediately before or after the current value.
                float before = this.ReadFloatAt(baseAddress + offset - 4);
                float after = this.ReadFloatAt(baseAddress + offset + 4);
                found.Append(string.Format("[0x{0:X3} {1} prev={2:0.#} next={3:0.#}] ",
                    offset, intMatch ? "int" : "flt", before, after));
            }

            // Health sits at 0x110 as a float, and the value after it is zero, so the
            // maximum is not immediately adjacent. Dump a window around it: a field that
            // holds steady at the starting health while 0x110 falls is the maximum.
            // 0x110 reads 100 on a ped whose reported health is 79, so it is the maximum
            // rather than the current value. Log it explicitly every time, so the pair can
            // be checked against each other directly.
            System.Text.StringBuilder window = new System.Text.StringBuilder();
            window.Append(string.Format("0x110={0:0.#} ", this.ReadFloatAt(baseAddress + 0x110)));
            for (int offset = 0x0F0; offset <= 0x150; offset += 4)
            {
                float value = this.ReadFloatAt(baseAddress + offset);
                if (value > 0.5f && value < 5000f)
                {
                    window.Append(string.Format("{0:X3}={1:0.#} ", offset, value));
                }
            }

            this.Log(string.Format("probe health={0} scanned=0x{1:X} hits={2}| window={3}",
                health, limit, found.ToString(), window.ToString()));
        }
        catch (Exception ex)
        {
            this.Log("probe failed: " + ex.Message);
        }
    }

    private float ReadFloatAt(int address)
    {
        try
        {
            int raw = System.Runtime.InteropServices.Marshal.ReadInt32((IntPtr)address);
            return BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
        }
        catch
        {
            return float.NaN;
        }
    }

    // What counts as full health for this ped, read from the ped itself. Exact from the
    // first frame, including for one that is already wounded when first aimed at, which is
    // the case the earlier learned-maximum version got wrong.
    private float FullHealthFor(Ped ped)
    {
        try
        {
            int address = ped.MemoryAddress;
            if (ReadableBytes(address + MaxHealthOffset, 4) == 4)
            {
                float maximum = this.ReadFloatAt(address + MaxHealthOffset);
                if (maximum >= MaxHealthLowest && maximum <= MaxHealthHighest)
                {
                    return maximum;
                }
            }
        }
        catch
        {
            // Fall through to the floor below.
        }

        return FullHealthFloor;
    }

    // The ped being aimed at, by lock-on first and free aim second.
    private Ped GetAimedAtPed()
    {
        Ped locked = Game.LocalPlayer.GetTargetedPed();
        if (locked != null && locked.Exists())
        {
            return locked;
        }

        // Free aim reports per ped rather than handing one back, so the candidates have to
        // be walked. Nearest first, so the answer is the one under the crosshair when
        // several line up.
        // Bounded: a capped list keeps this cheap, and every ped is validated before the
        // native sees it. An earlier version passed whatever World.GetPeds returned and
        // eventually handed the native a stale ped, which access-violated. That is a
        // corrupted-state exception, which an ordinary catch does not stop, so the script
        // was removed mid-session. Each ped is now isolated in its own try as well.
        Ped[] nearby = World.GetPeds(Player.Character.Position, MaxTargetDistance,
                                     MaxTargetsScanned);
        if (nearby == null)
        {
            return null;
        }

        Ped best = null;
        float bestDistance = float.MaxValue;

        foreach (Ped ped in nearby)
        {
            try
            {
                if (ped == null || !ped.Exists() || !ped.isAlive || ped == Player.Character)
                {
                    continue;
                }

                if (!GTA.Native.Function.Call<bool>("IS_PLAYER_FREE_AIMING_AT_CHAR",
                                                    Game.LocalPlayer, ped))
                {
                    continue;
                }

                float distance = ped.Position.DistanceTo(Player.Character.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = ped;
                }
            }
            catch
            {
                // One bad ped should not cost the whole scan.
            }
        }

        return best;
    }

    // Health is not drawn while the player is in a vehicle, and that matches the game
    // rather than falling short of it. The crosshair itself is still there, drawn as usual;
    // it is only the health ring around it that stays off.
    //
    // GTA IV does the same: it draws a crosshair in a car and never puts target health on
    // it. Observed 2026-09-24 in third person as well as under ZMenu IV's first person,
    // including while shooting. Both of those show target health on foot, so their silence
    // in a vehicle is the game's own behaviour rather than a limitation of either. So there
    // is nothing here to reproduce, and adding one would invent something the game does
    // not do.
    //
    // Drive-bys are free aim rather than lock-on, so the free-aim check above would be the
    // right one for them. In first person the player also only counts as aiming while
    // actually shooting, so even with a target the ring would flicker in and out shot by
    // shot.
    //
    // A geometric fallback was tried, picking whoever sat nearest the middle of the view,
    // and dropped. ZMenu IV's first person shows no target health in a vehicle either.

    // Draw the arc, clockwise from twelve o'clock, as a run of short lines.
    //
    // GTA.Graphics works in pixels, not in 0 to 1 screen space. LCPDFR's own Mouse.cs
    // passes Cursor.Position straight into DrawSprite, and its Gui.cs only divides by the
    // resolution when it calls the raw native rather than this managed wrapper. Drawing
    // this ring in fractions put it in the top left corner as a speck, which is what "it
    // never draws" turned out to mean. Pixels also make the circle round for free, with no
    // aspect correction needed.
    private void DrawHealthRing(GTA.Graphics graphics, float fraction)
    {
        Size resolution = Game.Resolution;
        if (resolution.Width <= 0 || resolution.Height <= 0)
        {
            return;
        }

        float centerX = resolution.Width / 2f + OffsetX * resolution.Height;
        float centerY = resolution.Height / 2f + OffsetY * resolution.Height;
        float radius = RingRadius * resolution.Height;
        float thickness = RingThickness * resolution.Height;

        int filled = (int)Math.Round(Segments * fraction);
        if (filled < 1 && fraction > 0f)
        {
            filled = 1;
        }

        Color color = HealthColor(fraction);

        for (int i = 0; i < filled; i++)
        {
            double from = this.SegmentAngle(i);
            double to = this.SegmentAngle(i + 1);

            float x1 = centerX + (float)Math.Sin(from) * radius;
            float y1 = centerY - (float)Math.Cos(from) * radius;
            float x2 = centerX + (float)Math.Sin(to) * radius;
            float y2 = centerY - (float)Math.Cos(to) * radius;

            graphics.DrawLine(x1, y1, x2, y2, thickness, color);
        }
    }

    // Segment index to angle in radians, starting at twelve o'clock and going clockwise.
    private double SegmentAngle(int index)
    {
        return (index / (double)Segments) * Math.PI * 2.0;
    }

    // Blend between the game's own health green and a matching muted red, so a wounded
    // target reads as darker and redder without ever leaving GTA IV's palette.
    private static Color HealthColor(float fraction)
    {
        int red = (int)(HurtColor.R + (HealthyColor.R - HurtColor.R) * fraction);
        int green = (int)(HurtColor.G + (HealthyColor.G - HurtColor.G) * fraction);
        int blue = (int)(HurtColor.B + (HealthyColor.B - HurtColor.B) * fraction);

        return Color.FromArgb(HealthyColor.A, Clamp(red), Clamp(green), Clamp(blue));
    }

    private static int Clamp(int value)
    {
        if (value < 0)
        {
            return 0;
        }
        if (value > 255)
        {
            return 255;
        }
        return value;
    }
}
