using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UltrakillTAS
{
    /// <summary>
    /// Every action the player can perform in ULTRAKILL, taken from the game's own
    /// InputActions asset (maps Movement, Fist, Weapon, HUD and UI). 64 bits wide:
    /// the original 32 no longer fit once fists, weapon cycling and variations joined.
    /// </summary>
    [Flags]
    public enum TasButtons : ulong
    {
        None = 0UL,
        Forward = 1UL << 0,
        Back = 1UL << 1,
        Left = 1UL << 2,
        Right = 1UL << 3,
        Jump = 1UL << 4,
        Slide = 1UL << 5,
        Dash = 1UL << 6,
        Fire1 = 1UL << 7,
        Fire2 = 1UL << 8,
        Punch = 1UL << 9,
        Whiplash = 1UL << 10,
        /// <summary>Weapon slots 1-6: revolver, shotgun, nailgun, railcannon, rocket launcher, spawner arm.</summary>
        Slot1 = 1UL << 11,
        Slot2 = 1UL << 12,
        Slot3 = 1UL << 13,
        Slot4 = 1UL << 14,
        Slot5 = 1UL << 15,
        Slot6 = 1UL << 16,
        MenuUp = 1UL << 17,
        MenuDown = 1UL << 18,
        MenuLeft = 1UL << 19,
        MenuRight = 1UL << 20,
        MenuConfirm = 1UL << 21,
        MenuCancel = 1UL << 22,
        MenuPause = 1UL << 23,
        /// <summary>Cycle between the arms (blue feedbacker / red knuckleblaster).</summary>
        FistChange = 1UL << 24,
        /// <summary>Select the feedbacker directly.</summary>
        FistFeedbacker = 1UL << 25,
        /// <summary>Select the knuckleblaster directly.</summary>
        FistKnuckleblaster = 1UL << 26,
        /// <summary>Mouse-wheel up / next weapon.</summary>
        WeaponNext = 1UL << 27,
        /// <summary>Mouse-wheel down / previous weapon.</summary>
        WeaponPrev = 1UL << 28,
        /// <summary>Swap back to the last used weapon.</summary>
        WeaponLast = 1UL << 29,
        VariationNext = 1UL << 30,
        VariationPrev = 1UL << 31,
        /// <summary>
        /// Pick variation 1-3 of the current weapon directly. This is the game's variation
        /// bind: pressing it re-equips the variation even when it is already equipped, which
        /// resets the weapon's firing cooldown on that tick.
        /// </summary>
        Variant1 = 1UL << 32,
        Variant2 = 1UL << 33,
        Variant3 = 1UL << 34,
        /// <summary>Show the stats / level info overlay (Tab).</summary>
        Stats = 1UL << 35,
        /// <summary>
        /// Script-only token 'G1' (shotgun swap), not a game action. While held, the engine
        /// watches the game for a shotgun shot (Shotgun.gunReady) and after each shot presses
        /// the variation bind of the equipped shotgun variation, re-equipping that same
        /// variation immediately and resetting its firing cooldown.
        /// </summary>
        ShotgunSwap1 = 1UL << 36,
        /// <summary>
        /// Script-only token 'G2' (same-tick shotgun swap), not a game action. The engine
        /// presses fire, selects the other variation and selects the original variation on
        /// one tick (drawing the shotgun first on earlier ticks when another weapon is out).
        /// </summary>
        ShotgunSwap2 = 1UL << 37
    }
    /// <summary>
    /// Field 1: the ten-slot autofunction column, "----------" by default.
    ///
    ///   slot 1  -  reserved (always '-')
    ///   slot 2  j  autojump       (jump the exact tick the player lands)
    ///   slot 3  s  autoslide      (hold slide on every grounded tick, from the landing tick)
    ///           S  spam autoslide (same gate, but press Slide on alternating eligible ticks)
    ///   slot 4  d  autodash       (dash on the optimal recovery tick)
    ///   slot 5  c  coin aim assist(snap viewangles to the nearest coin on fire ticks)
    ///   slot 6  g  ground slam    (hold slide in the air while a slam is valid; never on the ground)
    ///   slot 7  w  whiplash assist(hook when in range)
    ///   slot 8  t  cointhrow       (solve the coin toss angle, once per framebulk)
    ///   slot 9  e  deadcoin        (shoot a coin, then toss the follow-up inside its dead window)
    ///   slot 10 m  ssj macro       (slide one tick, wait N blank ticks, then jump)
    ///
    /// The letters are parsed in any position (see TasInputColumns.ParseAutofuncs). They act
    /// during script playback (Playing, and single steps while Paused) in TasEngine.ExecuteTick:
    /// 'j' 's' 'S' 'd' 'g' 'm' in ApplyMovementAutofuncs, 'c' 'w' in ApplyAimAutofuncs, and 't'
    /// in ApplyCoinThrow, which runs only on the first tick of the framebulk that holds it.
    /// Standalone and Recording run no framebulks, so no autofunc applies there.
    ///
    /// 'm' is followed by an optional variant digit: 'm' / 'm0' is the 3-tick perfect SSJ
    /// (slide, one blank release, jump), 'm1' adds one more blank tick for the 4-tick half boost,
    /// and 'm2' adds two for the 5-tick quarter boost. The digit is part of field 1, so an mN
    /// variant may make that field 11 characters long; the parser accepts any field length.
    /// </summary>
    [Flags]
    public enum TasAutofuncs : uint
    {
        None = 0u,
        // 1u << 0 (slot 1) is unused.
        AutoJump = 1u << 1,
        /// <summary>Slot 3 's': hold Slide on every grounded tick.</summary>
        AutoSlide = 1u << 2,
        AutoDash = 1u << 3,
        CoinAim = 1u << 4,
        GroundSlam = 1u << 5,
        WhipAssist = 1u << 6,
        /// <summary>
        /// Slot 8 't': coin throw. Once, on the first tick of its framebulk, simulate the
        /// game's toss and set the view angles so the coin stays closest to V1 for the longest.
        /// </summary>
        CoinThrow = 1u << 7,
        /// <summary>
        /// Slot 9 'e': deadcoin. On the first tick of its framebulk, shoot the nearest unshot
        /// coin; a few ticks later toss a second coin while it is still unhittable, so the shot's
        /// chain transfers its power to it without ricoshoting.
        /// </summary>
        Deadcoin = 1u << 8,
        /// <summary>
        /// Slot 10 'm': SSJ macro. One slide tick on the landing frame, then N blank ticks with
        /// Slide and Jump released, then Jump. 'm' / 'm0' is the perfect 3-tick pattern; 'm1'
        /// waits one extra blank tick (4 ticks, half boost), 'm2' waits two (5 ticks, quarter
        /// boost). The slide starts again on the landing tick, so the pattern repeats.
        /// </summary>
        SSJMacro = 1u << 9,
        /// <summary>
        /// The optional variant digit after 'm' is stored in bits 10-13 (0-9 blank waits).
        /// These bits are only meaningful together with <see cref="SSJMacro"/>.
        /// </summary>
        SSJMacroVariantMask = 0xFu << 10,
        /// <summary>Everything that makes up the SSJ macro setting, including its variant.</summary>
        SSJMacroMask = SSJMacro | SSJMacroVariantMask,
        /// <summary>
        /// Slot 3 'S': spam autoslide. It uses the same grounded/jump/SSJ gate as 's', but presses
        /// Slide only on alternate eligible ticks instead of holding it.
        /// </summary>
        AutoSlideSpam = 1u << 14,
        /// <summary>Both autoslide forms; a parsed field normally sets only one.</summary>
        AutoSlideMask = AutoSlide | AutoSlideSpam
    }

    public enum TasStartType
    {
        None,
        RestartMission,
        LoadLevel,
        LoadState,
        /// <summary>Load the title screen / main menu so a script can click through the menus.</summary>
        MainMenu
    }

    /// <summary>
    /// The "fist" header: the arm equipped before tick 0. Values match the game's arm
    /// variant numbers in FistControl (0 = blue Feedbacker, 1 = red Knuckleblaster).
    /// </summary>
    public enum TasStartFist
    {
        /// <summary>No header: the run keeps whatever fist is equipped.</summary>
        Unchanged = -1,
        /// <summary>"fist parry": the Feedbacker (blue arm).</summary>
        Feedbacker = 0,
        /// <summary>"fist knuckleblaster": the Knuckleblaster (red arm).</summary>
        Knuckleblaster = 1
    }

    /// <summary>
    /// The "difficulty" header: the difficulty set before the start scene loads. Values match
    /// the game's GameDifficulty enum (Harmless 0 .. Brutal 4), stored in PrefsManager "difficulty".
    /// </summary>
    public enum TasDifficulty
    {
        /// <summary>No header: the run keeps the game's current difficulty.</summary>
        Unchanged = -1,
        Harmless = 0,
        Lenient = 1,
        Standard = 2,
        Violent = 3,
        Brutal = 4
    }

    /// <summary>Which kind of statement a line in the frames section is.</summary>
    public enum TasFrameKind
    {
        /// <summary>A normal eight-field framebulk.</summary>
        Framebulk,
        /// <summary>save &lt;name&gt;</summary>
        Save,
        /// <summary>seed &lt;number&gt;</summary>
        Seed,
        /// <summary>change yaw to &lt;value&gt; over &lt;time&gt; s</summary>
        ChangeYaw,
        /// <summary>change pitch to &lt;value&gt; over &lt;time&gt; s</summary>
        ChangePitch,
        /// <summary>target_yaw &lt;value&gt;</summary>
        TargetYaw,
        /// <summary>norefresh [label]: a checkpoint. Takes no ticks; marks where cached playback resumes.</summary>
        Checkpoint,
        /// <summary>ff: takes no ticks. Every tick above it plays uncapped; from it onward the menu speed applies.</summary>
        FastForwardEnd,
        /// <summary>
        /// FFF: takes no ticks. The GoldSrc "norefresh" breakpoint: every tick before it runs
        /// headless (no 3D rendering, no wall-clock pacing, as fast as the CPU allows) and
        /// rendering is restored on the framebulk that immediately follows the marker.
        /// </summary>
        NoRefreshBreak,
        /// <summary>
        /// look_at entity &lt;id&gt; [hitbox] [ox oy oz] / look_at off: takes no ticks.
        /// While active, a '-' angle column aims the camera at the selected hitbox
        /// (or the target's center) plus the offset.
        /// </summary>
        LookAt,
        /// <summary>
        /// fist &lt;arm&gt;: takes no ticks. Selects the Feedbacker or Knuckleblaster on the first tick
        /// of the next framebulk by injecting the game's normal change-fist action when the
        /// requested arm is not already equipped.
        /// </summary>
        Fist,
        /// <summary>
        /// bake_start [name] / bake_start_nme [name]: takes no ticks. Opens a bake block; the ticks
        /// up to the matching bake_end are recorded into (or played back from) a .ukbake sidecar
        /// (see BakeManager). The _nme form leaves doors and streamed map rooms out of the bake.
        /// </summary>
        BakeStart,
        /// <summary>bake_end: takes no ticks. Closes the open bake block.</summary>
        BakeEnd,
        /// <summary>
        /// record_video &lt;file&gt;: takes no ticks. Starts a video recording to the file at this
        /// point of the run (same as the "bxt_rec start &lt;file&gt;" command).
        /// </summary>
        RecordVideoStart,
        /// <summary>record_video_stop: takes no ticks. Stops the video recording.</summary>
        RecordVideoStop
    }

    public sealed class TasParseException : Exception
    {
        public int LineNumber { get; private set; }
        public string SourceLine { get; private set; }

        public TasParseException(int lineNumber, string sourceLine, string message)
            : base("Line " + lineNumber.ToString(CultureInfo.InvariantCulture) + ": " + message)
        {
            LineNumber = lineNumber;
            SourceLine = sourceLine ?? string.Empty;
        }
    }

    /// <summary>
    /// Field 1, 2 and 3 of the canonical eight-field framebulk. Every field is a
    /// fixed-width dash string exactly like BunnyMod XT writes them:
    ///
    ///   field 1  ----------   autofuncs  [-][j][s][d][c][g][w][-][-][mN]
    ///   field 2  ------       movement   [f][l][r][b][d][s]
    ///   field 3  ------       actions    [j][1][2][p][v][w]
    ///
    /// Parsing is positional for the canonical widths, and also accepts a compact
    /// string where the same characters appear in any order ("f-r---" or "fr-----",
    /// "-1p---" or "1p-----"). A '-' or '.' is an inactive slot.
    ///
    /// ULTRAKILL extension: menu navigation has no slot in the six action columns,
    /// so those flags are appended after them as uppercase letters
    /// (U D L R C B E = menu up/down/left/right/confirm/cancel/pause) and a weapon
    /// slot number follows the 'w' token ("-----w3").
    /// </summary>
    public static class TasInputColumns
    {
        public const string NeutralAutofuncs = "----------";
        public const string NeutralMovement = "------";
        public const string NeutralActions = "------";

        // Ten base slots. An SSJ variant is written directly after 'm' ('---------m1'), so
        // that one field may be eleven characters long; parsing never enforced the width.
        public const int AutofuncWidth = 10;
        public const int MovementWidth = 6;
        public const int ActionWidth = 6;

        static readonly char[] AutofuncOrder = { 'j', 's', 'd', 'c', 'g', 'w', 't', 'e', 'm' };
        static readonly TasAutofuncs[] AutofuncFlags =
        {
            TasAutofuncs.AutoJump, TasAutofuncs.AutoSlide,
            TasAutofuncs.AutoDash, TasAutofuncs.CoinAim, TasAutofuncs.GroundSlam,
            TasAutofuncs.WhipAssist, TasAutofuncs.CoinThrow, TasAutofuncs.Deadcoin,
            TasAutofuncs.SSJMacro
        };
        // Written column of each letter: slot 1 stays '-', so j is slot 2 ... e is slot 9, m is slot 10.
        static readonly int[] AutofuncSlots = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        // ------------------------------------------------------------------
        // Field 1 - autofuncs
        // ------------------------------------------------------------------

        public static TasAutofuncs ParseAutofuncs(string text, int lineNumber, string sourceLine)
        {
            TasAutofuncs result = TasAutofuncs.None;
            if (string.IsNullOrEmpty(text))
                return result;

            string field = text.Trim();
            for (int i = 0; i < field.Length; i++)
            {
                // 'S' is the one case-sensitive autofunc: lowercase s holds Slide, uppercase S
                // spam-presses it. Every other letter keeps its old case-insensitive handling.
                if (field[i] == 'S')
                {
                    result &= ~TasAutofuncs.AutoSlide;
                    result |= TasAutofuncs.AutoSlideSpam;
                    continue;
                }

                char c = char.ToLowerInvariant(field[i]);
                if (c == '-' || c == '.' || c == ' ')
                    continue;

                if (c == 's')
                {
                    result &= ~TasAutofuncs.AutoSlideSpam;
                    result |= TasAutofuncs.AutoSlide;
                    continue;
                }

                if (c == 'm')
                {
                    // The optional digit after m selects the SSJ variant: m/m0 is the perfect
                    // three-tick pattern, m1 adds a blank wait, m2 adds two, and so on. A bare m
                    // is variant 0, so old scripts keep their behaviour.
                    result |= TasAutofuncs.SSJMacro;
                    result &= ~TasAutofuncs.SSJMacroVariantMask;
                    if (i + 1 < field.Length && field[i + 1] >= '0' && field[i + 1] <= '9')
                    {
                        int blanks = field[i + 1] - '0';
                        result |= (TasAutofuncs)((uint)blanks << 10);
                        i++;
                    }
                    continue;
                }

                int slot = Array.IndexOf(AutofuncOrder, c);
                if (slot < 0)
                    throw new TasParseException(lineNumber, sourceLine,
                        "unknown autofunc '" + field[i] + "' in field 1 (valid: j s S d c g w t e m[0-9], - for inactive)");
                result |= AutofuncFlags[slot];
            }
            return result;
        }

        /// <summary>Blank ticks requested by an mN SSJ variant (0 for bare m).</summary>
        public static int SsjMacroBlankFrames(TasAutofuncs value)
        {
            return (int)(((uint)value & (uint)TasAutofuncs.SSJMacroVariantMask) >> 10);
        }

        public static string FormatAutofuncs(TasAutofuncs value)
        {
            char[] slots = new char[AutofuncWidth];
            for (int i = 0; i < slots.Length; i++)
                slots[i] = '-';
            for (int i = 0; i < AutofuncOrder.Length; i++)
                if ((value & AutofuncFlags[i]) != 0)
                    slots[AutofuncSlots[i]] = AutofuncOrder[i];
            // Both bits could be present in a hand-built value; uppercase S is the spam form.
            if ((value & TasAutofuncs.AutoSlideSpam) != 0)
                slots[AutofuncSlots[1]] = 'S';
            string field = new string(slots);
            int blanks = SsjMacroBlankFrames(value);
            if ((value & TasAutofuncs.SSJMacro) != 0 && blanks > 0)
                field += (char)('0' + blanks);
            return field;
        }

        // ------------------------------------------------------------------
        // Field 2 - movement
        // ------------------------------------------------------------------

        public static TasButtons ParseMovement(string text, int lineNumber, string sourceLine)
        {
            TasButtons result = TasButtons.None;
            if (string.IsNullOrEmpty(text))
                return result;

            string field = text.Trim();
            for (int i = 0; i < field.Length; i++)
            {
                char c = char.ToLowerInvariant(field[i]);
                switch (c)
                {
                    case '-':
                    case '.':
                    case ' ':
                        break;
                    case 'f': result |= TasButtons.Forward; break;
                    case 'l': result |= TasButtons.Left; break;
                    case 'r': result |= TasButtons.Right; break;
                    case 'b': result |= TasButtons.Back; break;
                    case 'd': result |= TasButtons.Dash; break;
                    case 's': result |= TasButtons.Slide; break;
                    default:
                        throw new TasParseException(lineNumber, sourceLine,
                            "unknown movement key '" + field[i] + "' in field 2 (valid: f l r b d s, - for neutral)");
                }
            }
            return result;
        }

        public static string FormatMovement(TasButtons value)
        {
            char[] slots = NeutralMovement.ToCharArray();
            if ((value & TasButtons.Forward) != 0) slots[0] = 'f';
            if ((value & TasButtons.Left) != 0) slots[1] = 'l';
            if ((value & TasButtons.Right) != 0) slots[2] = 'r';
            if ((value & TasButtons.Back) != 0) slots[3] = 'b';
            if ((value & TasButtons.Dash) != 0) slots[4] = 'd';
            if ((value & TasButtons.Slide) != 0) slots[5] = 's';
            return new string(slots);
        }

        // ------------------------------------------------------------------
        // Field 3 - actions
        // ------------------------------------------------------------------

        public static TasButtons ParseActions(string text, int lineNumber, string sourceLine)
        {
            TasButtons result = TasButtons.None;
            if (string.IsNullOrEmpty(text))
                return result;

            string field = text.Trim();
            bool weaponTokenSeen = false;
            int weaponSlot = 0;

            for (int i = 0; i < field.Length; i++)
            {
                char c = field[i];
                if (c == '-' || c == '.' || c == ' ')
                    continue;

                switch (c)
                {
                    case 'j': result |= TasButtons.Jump; break;
                    case '1': result |= TasButtons.Fire1; break;
                    case '2': result |= TasButtons.Fire2; break;
                    case 'p': result |= TasButtons.Punch; break;
                    case 'v':
                        // 'v1' / 'v2' / 'v3' are the variation binds (blue, green, red). A
                        // bare 'v' is still whiplash. The canonical whiplash slot is base
                        // slot 5 (index 4), where 'v' stays whiplash even before a digit.
                        if (i != 4 && i + 1 < field.Length && field[i + 1] >= '1' && field[i + 1] <= '3')
                        {
                            int variation = field[i + 1] - '0';
                            result |= variation == 1 ? TasButtons.Variant1 :
                                      (variation == 2 ? TasButtons.Variant2 : TasButtons.Variant3);
                            i++;
                        }
                        else
                        {
                            result |= TasButtons.Whiplash;
                        }
                        break;
                    case 'w':
                    case 'W':
                        weaponTokenSeen = true;
                        weaponSlot = 0;
                        // "w3" (alias of a weapon slot press) reads the slot digit
                        // that follows the token.
                        if (i + 1 < field.Length && char.IsDigit(field[i + 1]))
                        {
                            weaponSlot = field[i + 1] - '0';
                            i++;
                        }
                        break;
                    case 'U': result |= TasButtons.MenuUp; break;
                    case 'D': result |= TasButtons.MenuDown; break;
                    case 'L': result |= TasButtons.MenuLeft; break;
                    case 'R': result |= TasButtons.MenuRight; break;
                    case 'C': result |= TasButtons.MenuConfirm; break;
                    case 'B': result |= TasButtons.MenuCancel; break;
                    case 'E': result |= TasButtons.MenuPause; break;
                    // Extension tokens: every remaining ULTRAKILL action.
                    case 'x': result |= TasButtons.FistChange; break;
                    case 'F': result |= TasButtons.FistFeedbacker; break;
                    case 'K': result |= TasButtons.FistKnuckleblaster; break;
                    case 'N': result |= TasButtons.WeaponNext; break;
                    case 'P': result |= TasButtons.WeaponPrev; break;
                    case 'T': result |= TasButtons.WeaponLast; break;
                    case '>': result |= TasButtons.VariationNext; break;
                    case '<': result |= TasButtons.VariationPrev; break;
                    case 'S': result |= TasButtons.Stats; break;
                    case 'G':
                    {
                        // "G1" / "G2" read the mode digit that follows, like "y1".
                        int mode = i + 1 < field.Length && char.IsDigit(field[i + 1]) ? field[i + 1] - '0' : 0;
                        if (mode < 1 || mode > 2)
                            throw new TasParseException(lineNumber, sourceLine,
                                "token 'G' in field 3 needs a shotgun swap mode 1-2 (G1 = swap after each shot, G2 = same-tick swap)");
                        result |= mode == 1 ? TasButtons.ShotgunSwap1 : TasButtons.ShotgunSwap2;
                        i++;
                        break;
                    }
                    case 'y':
                    {
                        int variant = i + 1 < field.Length && char.IsDigit(field[i + 1]) ? field[i + 1] - '0' : 0;
                        if (variant < 1 || variant > 3)
                            throw new TasParseException(lineNumber, sourceLine,
                                "token 'y' in field 3 needs a variation number 1-3 (e.g. y2)");
                        result |= variant == 1 ? TasButtons.Variant1 : (variant == 2 ? TasButtons.Variant2 : TasButtons.Variant3);
                        i++;
                        break;
                    }
                    default:
                        throw new TasParseException(lineNumber, sourceLine,
                            "unknown action key '" + c + "' in field 3 (valid: j 1 2 p v w[N] v1 v2 v3 x F K N P T < > y[1-3] S G[1-2], uppercase U D L R C B E for menus, - for neutral)");
                }
            }

            if (weaponTokenSeen)
            {
                if (weaponSlot >= 1 && weaponSlot <= 6)
                    result |= SlotFlag(weaponSlot);
                else
                    throw new TasParseException(lineNumber, sourceLine,
                        "weapon token 'w' in field 3 needs a slot number 1-6 (e.g. -----w3)");
            }
            return result;
        }

        public static string FormatActions(TasButtons value)
        {
            StringBuilder sb = new StringBuilder(8);
            sb.Append((value & TasButtons.Jump) != 0 ? 'j' : '-');
            sb.Append((value & TasButtons.Fire1) != 0 ? '1' : '-');
            sb.Append((value & TasButtons.Fire2) != 0 ? '2' : '-');
            sb.Append((value & TasButtons.Punch) != 0 ? 'p' : '-');
            sb.Append((value & TasButtons.Whiplash) != 0 ? 'v' : '-');

            int slot = WeaponSlotOf(value);
            sb.Append(slot > 0 ? 'w' : '-');
            if (slot > 0)
                sb.Append((char)('0' + slot));

            if ((value & TasButtons.MenuUp) != 0) sb.Append('U');
            if ((value & TasButtons.MenuDown) != 0) sb.Append('D');
            if ((value & TasButtons.MenuLeft) != 0) sb.Append('L');
            if ((value & TasButtons.MenuRight) != 0) sb.Append('R');
            if ((value & TasButtons.MenuConfirm) != 0) sb.Append('C');
            if ((value & TasButtons.MenuCancel) != 0) sb.Append('B');
            if ((value & TasButtons.MenuPause) != 0) sb.Append('E');
            if ((value & TasButtons.FistChange) != 0) sb.Append('x');
            if ((value & TasButtons.FistFeedbacker) != 0) sb.Append('F');
            if ((value & TasButtons.FistKnuckleblaster) != 0) sb.Append('K');
            if ((value & TasButtons.WeaponNext) != 0) sb.Append('N');
            if ((value & TasButtons.WeaponPrev) != 0) sb.Append('P');
            if ((value & TasButtons.WeaponLast) != 0) sb.Append('T');
            if ((value & TasButtons.VariationPrev) != 0) sb.Append('<');
            if ((value & TasButtons.VariationNext) != 0) sb.Append('>');
            // Canonical output keeps the historical y1/y2/y3 spelling. The parser accepts
            // v1/v2/v3 as aliases of the same variation binds.
            if ((value & TasButtons.Variant1) != 0) sb.Append("y1");
            if ((value & TasButtons.Variant2) != 0) sb.Append("y2");
            if ((value & TasButtons.Variant3) != 0) sb.Append("y3");
            if ((value & TasButtons.Stats) != 0) sb.Append('S');
            if ((value & TasButtons.ShotgunSwap1) != 0) sb.Append("G1");
            if ((value & TasButtons.ShotgunSwap2) != 0) sb.Append("G2");
            return sb.ToString();
        }

        /// <summary>1-6 when the frame presses a weapon slot, otherwise 0.</summary>
        public static int WeaponSlotOf(TasButtons value)
        {
            if ((value & TasButtons.Slot1) != 0) return 1;
            if ((value & TasButtons.Slot2) != 0) return 2;
            if ((value & TasButtons.Slot3) != 0) return 3;
            if ((value & TasButtons.Slot4) != 0) return 4;
            if ((value & TasButtons.Slot5) != 0) return 5;
            if ((value & TasButtons.Slot6) != 0) return 6;
            return 0;
        }

        public static TasButtons SlotFlag(int slot)
        {
            switch (slot)
            {
                case 1: return TasButtons.Slot1;
                case 2: return TasButtons.Slot2;
                case 3: return TasButtons.Slot3;
                case 4: return TasButtons.Slot4;
                case 5: return TasButtons.Slot5;
                case 6: return TasButtons.Slot6;
                default: return TasButtons.None;
            }
        }

        /// <summary>Compact one-line form used by the HUD: "f-r---|1----w3".</summary>
        public static string FormatCompact(TasButtons value)
        {
            return FormatMovement(value) + "|" + FormatActions(value);
        }

        /// <summary>Parses either one field or the "movement|actions" pair.</summary>
        public static TasButtons ParseCompact(string text, int lineNumber, string sourceLine)
        {
            if (string.IsNullOrEmpty(text))
                return TasButtons.None;
            int pipe = text.IndexOf('|');
            if (pipe < 0)
                return ParseMovement(text, lineNumber, sourceLine) | ParseActions(text, lineNumber, sourceLine);
            return ParseMovement(text.Substring(0, pipe), lineNumber, sourceLine) |
                   ParseActions(text.Substring(pipe + 1), lineNumber, sourceLine);
        }
    }

    /// <summary>Absolute angle, relative delta ("d+2.0"), or unchanged ("-").</summary>
    public sealed class AngleToken
    {
        public bool IsDelta;
        public bool Unchanged;
        public double Value;
        public string Raw = "-";

        public double Resolve(double current)
        {
            if (Unchanged)
                return current;
            return IsDelta ? current + Value : Value;
        }

        public AngleToken Clone()
        {
            return new AngleToken { IsDelta = IsDelta, Unchanged = Unchanged, Value = Value, Raw = Raw };
        }

        public static AngleToken Absolute(double value)
        {
            return new AngleToken
            {
                IsDelta = false,
                Unchanged = false,
                Value = value,
                Raw = Format(value)
            };
        }

        public static AngleToken Delta(double value)
        {
            return new AngleToken
            {
                IsDelta = true,
                Unchanged = false,
                Value = value,
                Raw = "d" + (value >= 0 ? "+" : string.Empty) + Format(value)
            };
        }

        public static AngleToken Keep()
        {
            return new AngleToken { Unchanged = true, Raw = "-" };
        }

        public static AngleToken Parse(string token, string label, int lineNumber, string sourceLine)
        {
            if (string.IsNullOrEmpty(token))
                throw new TasParseException(lineNumber, sourceLine, "missing " + label + " angle");

            string text = token.Trim();
            if (text == "-" || text == "." || text.Length == 0)
                return Keep();

            bool delta = false;
            if (text.Length > 1 && (text[0] == 'd' || text[0] == 'D'))
            {
                char sign = text.Length > 1 ? text[1] : '+';
                if (sign == '+' || sign == '-' || char.IsDigit(sign) || sign == '.')
                {
                    delta = true;
                    text = text.Substring(1);
                }
            }
            if (text.Length > 0 && text[0] == '+')
                text = text.Substring(1);

            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value))
                throw new TasParseException(lineNumber, sourceLine,
                    "invalid " + label + " angle '" + token + "' (expected e.g. 90.0, d+1.5, d-0.5, or -)");

            return new AngleToken
            {
                IsDelta = delta,
                Unchanged = false,
                Value = value,
                Raw = (delta ? "d" + (value >= 0 ? "+" : string.Empty) : string.Empty) + Format(value)
            };
        }

        /// <summary>Canonical BunnyMod XT style: at least one decimal digit ("90.0", "12.5").</summary>
        public static string Format(double value)
        {
            return value.ToString("0.0######", CultureInfo.InvariantCulture);
        }

        public override string ToString()
        {
            return Raw;
        }
    }

    public sealed class TasProperties
    {
        /// <summary>
        /// One native ULTRAKILL physics step (125 Hz). The default frametime is one
        /// native step, so an unspecified Field 4 runs the physics 1:1.
        /// </summary>
        public const double DefaultHostFrametime = 0.008;

        public int Version = 1;
        public int Seed = 0;
        public double HostFrametime = DefaultHostFrametime;
        /// <summary>"demo &lt;file&gt;" header: the demo/recording this script was made against.</summary>
        public string Demo = string.Empty;
        /// <summary>"save &lt;name&gt;" header: the default named savestate for the script.</summary>
        public string SaveName = string.Empty;
        public string Author = string.Empty;
        public string Description = string.Empty;
        public TasStartType StartType = TasStartType.None;
        public string LoadLevelName = string.Empty;
        public int LoadStateSlot = 0;
        public string LoadCommand = string.Empty;
        public bool WaitForPlayerControl = true;
        /// <summary>"fist knuckleblaster" / "fist parry": the fist equipped before tick 0.</summary>
        public TasStartFist StartFist = TasStartFist.Unchanged;
        /// <summary>"difficulty violent" etc.: the game difficulty set before the start scene loads.</summary>
        public TasDifficulty Difficulty = TasDifficulty.Unchanged;
        /// <summary>
        /// "weapons rev0 sho1 arm1" header: the gear keys the player owns for the run, in the
        /// canonical <see cref="WeaponUnlock.GearKeys"/> order. Empty = no weapons ("weapons none").
        /// Null = not specified: the player's current ownership is kept.
        /// </summary>
        public List<string> Weapons;
        /// <summary>
        /// "loadout revolver=iii arm=iox" header: ForcedLoadout field name to its three-character
        /// blue/red/green code (i = IfEquipped, o = ForceOn, x = ForceOff). Null or empty = not
        /// specified: the game's current loadout is kept. Written in <see cref="WeaponUnlock.LoadoutFields"/> order.
        /// </summary>
        public Dictionary<string, string> Loadout;

        /// <summary>True when the loadout header has at least one field.</summary>
        public bool HasLoadout
        {
            get { return Loadout != null && Loadout.Count > 0; }
        }

        /// <summary>Unknown header keys are preserved so custom metadata survives a load/save cycle.</summary>
        public List<KeyValuePair<string, string>> Extras = new List<KeyValuePair<string, string>>();

        public double GetFrametime(bool hasOverride, double overrideValue)
        {
            return hasOverride ? overrideValue : HostFrametime;
        }
    }

    /// <summary>
    /// One authored line of the frames section: either an eight-field framebulk or
    /// one of the special frames (save / seed / change ... / target_yaw / fist).
    /// </summary>
    public sealed class TasFrameLine
    {
        public TasFrameKind Kind = TasFrameKind.Framebulk;

        /// <summary>Field 1.</summary>
        public TasAutofuncs Autofuncs = TasAutofuncs.None;

        /// <summary>Field 7. For special frames this is the number of ticks the statement spans.</summary>
        public int Count = 1;

        /// <summary>Field 4. '-' inherits host_frametime.</summary>
        public bool HasFrametimeOverride;
        public double FrametimeOverride;

        /// <summary>Fields 2 and 3 combined into the input the game receives.</summary>
        public TasButtons Buttons = TasButtons.None;

        /// <summary>Field 5.</summary>
        public AngleToken Yaw = AngleToken.Keep();

        /// <summary>Field 6.</summary>
        public AngleToken Pitch = AngleToken.Keep();

        /// <summary>Set by "target_yaw" lines; overrides Field 5 while active.</summary>
        public AngleToken TargetYaw = AngleToken.Keep();

        /// <summary>Field 8. Semicolon separated, run every tick unless prefixed "once:".</summary>
        public List<string> Commands = new List<string>();

        // Special frame payloads.
        public string SaveName = string.Empty;
        public int SeedValue;
        public double ChangeSeconds;

        /// <summary>Label of a norefresh line (may be empty).</summary>
        public string CheckpointLabel = string.Empty;

        /// <summary>look_at payload: false for "look_at off". The offset is in world units.</summary>
        public bool LookAtEnabled;
        public int LookAtEntity;
        /// <summary>look_at by name: nearest entity whose type (or category) matches this, e.g. "coin".</summary>
        public string LookAtName = string.Empty;
        /// <summary>Optional hitbox name ("head", "left_arm", ...); empty means the target's center.</summary>
        public string LookAtHitbox = string.Empty;
        /// <summary>ID the nearest-name lookup picked, and the tick it was picked on.</summary>
        public int LookAtResolvedId;
        public string LookAtResolvedName = string.Empty;
        public int LookAtResolvedTick = -1;
        public bool LookAtAnnounced;
        public double LookAtOffsetX;
        public double LookAtOffsetY;
        public double LookAtOffsetZ;
        /// <summary>"look_at x y z": aim at this fixed world coordinate (no entity involved).</summary>
        public bool LookAtIsPoint;
        public double LookAtPointX;
        public double LookAtPointY;
        public double LookAtPointZ;

        /// <summary>fist payload: the arm a "fist ..." special frame requests.</summary>
        public TasStartFist FistTarget = TasStartFist.Unchanged;

        /// <summary>Name of a bake_start / bake_start_nme line (may be empty: the block index names it).</summary>
        public string BakeName = string.Empty;

        /// <summary>
        /// True for bake_start_nme: doors and the streamed rooms they activate are map elements and
        /// are left out of the recorded/restored bake. Everything else is baked normally.
        /// </summary>
        public bool BakeExcludeMapElements;

        /// <summary>File name of a record_video line (a single token).</summary>
        public string RecordFile = string.Empty;

        public int SourceLine;

        public bool IsFramebulk { get { return Kind == TasFrameKind.Framebulk; } }

        public bool HasTargetYaw
        {
            get { return TargetYaw != null && !TargetYaw.Unchanged; }
        }

        public double EffectiveFrametime(double defaultFrametime)
        {
            return HasFrametimeOverride ? FrametimeOverride : defaultFrametime;
        }

        public TasFrameLine Clone()
        {
            TasFrameLine copy = new TasFrameLine
            {
                Kind = Kind,
                Autofuncs = Autofuncs,
                Count = Count,
                HasFrametimeOverride = HasFrametimeOverride,
                FrametimeOverride = FrametimeOverride,
                Buttons = Buttons,
                Pitch = Pitch.Clone(),
                Yaw = Yaw.Clone(),
                TargetYaw = TargetYaw.Clone(),
                SaveName = SaveName,
                SeedValue = SeedValue,
                ChangeSeconds = ChangeSeconds,
                CheckpointLabel = CheckpointLabel,
                LookAtEnabled = LookAtEnabled,
                LookAtEntity = LookAtEntity,
                LookAtName = LookAtName,
                LookAtHitbox = LookAtHitbox,
                LookAtOffsetX = LookAtOffsetX,
                LookAtOffsetY = LookAtOffsetY,
                LookAtOffsetZ = LookAtOffsetZ,
                LookAtIsPoint = LookAtIsPoint,
                LookAtPointX = LookAtPointX,
                LookAtPointY = LookAtPointY,
                LookAtPointZ = LookAtPointZ,
                FistTarget = FistTarget,
                BakeName = BakeName,
                BakeExcludeMapElements = BakeExcludeMapElements,
                RecordFile = RecordFile,
                SourceLine = SourceLine
            };
            copy.Commands.AddRange(Commands);
            return copy;
        }

        /// <summary>True when two lines hold the exact same simulation state and can merge.</summary>
        public bool BehaviourEquals(TasFrameLine other)
        {
            if (other == null)
                return false;
            // Only plain framebulks bulk together. Special frames carry no repeat
            // column in the file, so merging them silently changed their tick count.
            if (Kind != TasFrameKind.Framebulk || other.Kind != TasFrameKind.Framebulk)
                return false;
            if (Autofuncs != other.Autofuncs)
                return false;
            if (Buttons != other.Buttons || HasFrametimeOverride != other.HasFrametimeOverride)
                return false;
            if (HasFrametimeOverride && Math.Abs(FrametimeOverride - other.FrametimeOverride) > 1e-12)
                return false;
            if (!AngleEquals(Pitch, other.Pitch) || !AngleEquals(Yaw, other.Yaw) ||
                !AngleEquals(TargetYaw, other.TargetYaw))
                return false;
            if (!string.Equals(SaveName, other.SaveName, StringComparison.Ordinal))
                return false;
            if (SeedValue != other.SeedValue || Math.Abs(ChangeSeconds - other.ChangeSeconds) > 1e-12)
                return false;
            // Command lines never merge: their commands would repeat for the whole bulk.
            return Commands.Count == 0 && other.Commands.Count == 0;
        }

        static bool AngleEquals(AngleToken a, AngleToken b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Unchanged != b.Unchanged || a.IsDelta != b.IsDelta)
                return false;
            return Math.Abs(a.Value - b.Value) <= 1e-9;
        }

        /// <summary>
        /// Resolves one angle column for one tick. A '-' column under an active
        /// "change ... over N s" moves the angle toward the target: each tick covers
        /// frametime / remaining of the distance left, which is a straight line in time
        /// and lands exactly on the target on the last tick. It depends only on the
        /// previous angle and the time left, so a rewind or checkpoint restore in the
        /// middle of a change carries on from wherever the camera is. Any other value in
        /// the column (absolute or relative) overrides the change for that tick.
        /// </summary>
        public static double ResolveAngle(AngleToken token, double previous, TasChange change,
                                          double tickTime, double frametime)
        {
            if (change == null || !token.Unchanged)
                return token.Resolve(previous);
            double remaining = change.Seconds - (tickTime - change.StartTime);
            if (remaining <= frametime * 1.0001)
                return change.Final;
            return previous + (change.Final - previous) * (frametime / remaining);
        }

        /// <summary>
        /// The canonical eight-field record, without a trailing newline. The commands
        /// field is always present (empty when there are none).
        /// </summary>
        public string ToPipeLine()
        {
            if (Kind != TasFrameKind.Framebulk)
                return ToSpecialLine();

            StringBuilder sb = new StringBuilder(56);
            sb.Append(TasInputColumns.FormatAutofuncs(Autofuncs));
            sb.Append('|');
            sb.Append(TasInputColumns.FormatMovement(Buttons));
            sb.Append('|');
            sb.Append(TasInputColumns.FormatActions(Buttons));
            sb.Append('|');
            sb.Append(HasFrametimeOverride ? TasScript.FormatFrametime(FrametimeOverride) : "-");
            sb.Append('|');
            sb.Append(Yaw.Raw);
            sb.Append('|');
            sb.Append(Pitch.Raw);
            sb.Append('|');
            sb.Append(Count.ToString(CultureInfo.InvariantCulture));
            // Field 8 is always written, even when empty, so every saved framebulk has
            // exactly eight fields (seven pipes).
            sb.Append('|');
            for (int i = 0; i < Commands.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");
                sb.Append(Commands[i]);
            }
            return sb.ToString();
        }

        string ToSpecialLine()
        {
            switch (Kind)
            {
                case TasFrameKind.Save:
                    return "save " + SaveName;
                case TasFrameKind.Seed:
                    return "seed " + SeedValue.ToString(CultureInfo.InvariantCulture);
                case TasFrameKind.ChangeYaw:
                    return "change yaw to " + AngleToken.Format(Yaw.Value) +
                           " over " + TasScript.FormatFrametime(ChangeSeconds) + " s";
                case TasFrameKind.ChangePitch:
                    return "change pitch to " + AngleToken.Format(Pitch.Value) +
                           " over " + TasScript.FormatFrametime(ChangeSeconds) + " s";
                case TasFrameKind.TargetYaw:
                    return "target_yaw " + AngleToken.Format(TargetYaw.Value);
                case TasFrameKind.Checkpoint:
                    return CheckpointLabel.Length > 0 ? "norefresh " + CheckpointLabel : "norefresh";
                case TasFrameKind.FastForwardEnd:
                    return "ff";
                case TasFrameKind.NoRefreshBreak:
                    return "FFF";
                case TasFrameKind.Fist:
                    return "fist " + TasScript.FormatStartFist(FistTarget);
                case TasFrameKind.BakeStart:
                {
                    string command = BakeExcludeMapElements ? "bake_start_nme" : "bake_start";
                    return BakeName.Length > 0 ? command + " " + BakeName : command;
                }
                case TasFrameKind.BakeEnd:
                    return "bake_end";
                case TasFrameKind.RecordVideoStart:
                    return "record_video " + RecordFile;
                case TasFrameKind.RecordVideoStop:
                    return "record_video_stop";
                case TasFrameKind.LookAt:
                    if (!LookAtEnabled)
                        return "look_at off";
                    if (LookAtIsPoint)
                        return "look_at " + AngleToken.Format(LookAtPointX) + " " + AngleToken.Format(LookAtPointY) + " " +
                               AngleToken.Format(LookAtPointZ);
                    string hitbox = LookAtHitbox.Length > 0 ? " " + LookAtHitbox : string.Empty;
                    if (LookAtName.Length > 0)
                        return "look_at " + LookAtName + hitbox + " " +
                               AngleToken.Format(LookAtOffsetX) + " " + AngleToken.Format(LookAtOffsetY) + " " +
                               AngleToken.Format(LookAtOffsetZ);
                    return "look_at entity " + LookAtEntity.ToString(CultureInfo.InvariantCulture) + hitbox + " " +
                           AngleToken.Format(LookAtOffsetX) + " " + AngleToken.Format(LookAtOffsetY) + " " +
                           AngleToken.Format(LookAtOffsetZ);
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// A comment appended after the record when the editor shows a line, e.g.
        /// "  // 20 ticks". Never written to disk.
        /// </summary>
        public string ToDisplayLine()
        {
            string line = ToPipeLine();
            if (Kind != TasFrameKind.Framebulk)
                return line;
            return line + "   // " + Count.ToString(CultureInfo.InvariantCulture) + " tick" +
                   (Count == 1 ? string.Empty : "s");
        }
    }

    /// <summary>
    /// A "change yaw|pitch to &lt;v&gt; over &lt;t&gt; s" line. It takes no ticks: it is active for
    /// the framebulks that follow it, whose '-' angle column it drives.
    /// </summary>
    public sealed class TasChange
    {
        public bool IsYaw;
        public double Final;
        public double Seconds;
        /// <summary>Tick and script time of the first framebulk tick it applies to.</summary>
        public int StartTick;
        public double StartTime;
    }

    /// <summary>A norefresh line: where cached playback resumes, and the checksum that guards it.</summary>
    public sealed class TasCheckpoint
    {
        public int LineIndex;
        /// <summary>Absolute tick of the framebulk that follows the marker.</summary>
        public int Tick;
        public string Label = string.Empty;
        /// <summary>Checksum of the header and every line before the marker.</summary>
        public string Hash = string.Empty;
    }

    /// <summary>
    /// A bake_start / bake_start_nme ... bake_end block: the ticks it covers and the checksum that
    /// guards its .ukbake sidecar. Built with the timeline; see TasScript.BakeBlocks.
    /// </summary>
    public sealed class TasBakeBlock
    {
        /// <summary>0-based position among the script's blocks.</summary>
        public int Index;
        /// <summary>Name from the bake_start line, or empty.</summary>
        public string Name = string.Empty;
        /// <summary>True for a bake_start_nme block: map elements (doors and streamed rooms) are not baked.</summary>
        public bool ExcludeMapElements;
        /// <summary>Name used for the sidecar file: the given name, else "block&lt;Index+1&gt;".</summary>
        public string FileName = string.Empty;
        public int StartLineIndex;
        public int EndLineIndex;
        /// <summary>First tick inside the block.</summary>
        public int StartTick;
        /// <summary>First tick after the block (exclusive).</summary>
        public int EndTick;
        /// <summary>SHA-256 (lower-case hex) of the header and every line from the script start through bake_end.</summary>
        public string Hash = string.Empty;
        /// <summary>Old hash used before fist selection left the bake hash; empty unless a fist header is set.</summary>
        public string LegacyHash = string.Empty;

        public int TickCount { get { return EndTick - StartTick; } }
    }

    public struct TickLocation
    {
        public int LineIndex;
        public int TickWithinLine;
        public int AbsoluteTick;
        public double Time;
        public bool Valid;
    }

    /// <summary>
    /// In-memory .uktas / .hltas document using the canonical BunnyMod XT
    /// eight-field pipe layout, adapted to ULTRAKILL mechanics.
    ///
    /// Properties block:
    ///   version 1
    ///   host_frametime 0.008
    ///   seed 1337
    ///   demo mydemo.ukdemo
    ///   save mysave
    ///   start_type restart_mission
    ///   load_command bxt_timer_reset
    ///   frames
    ///
    /// Framebulk:
    ///   autofuncs|movement|actions|frametime|yaw|pitch|frames|commands
    ///   ----------|------|------|0.001|90.0|0.0|20|echo coin setup
    ///
    /// Special frames inside the frames section:
    ///   save &lt;name&gt;
    ///   seed &lt;number&gt;
    ///   change yaw to &lt;value&gt; over &lt;time&gt; s
    ///   change pitch to &lt;value&gt; over &lt;time&gt; s
    ///   target_yaw &lt;value&gt;
    ///   fist parry / fist knuckleblaster
    ///   look_at entity &lt;id&gt; [hitbox] [ox oy oz]
    ///   look_at &lt;type|category&gt; [hitbox] [ox oy oz]
    ///   look_at &lt;x&gt; &lt;y&gt; &lt;z&gt;
    ///   look_at off
    ///   bake_start [name] / bake_start_nme [name]
    ///   bake_end
    ///   FFF
    ///
    /// FFF is the GoldSrc r_norefresh breakpoint: the ticks before it are simulated with the
    /// 3D screen switched off, and rendering resumes on the framebulk that follows it.
    /// </summary>
    public sealed class TasScript
    {
        public const double MinFrametime = 0.0001;
        public const double MaxFrametime = 1.0;
        public const int MaxTicksPerLine = 1000000;

        public TasProperties Properties = new TasProperties();
        public List<TasFrameLine> Lines = new List<TasFrameLine>();
        public List<string> Warnings = new List<string>();
        public string SourceName = string.Empty;

        bool _timelineDirty = true;
        int[] _lineStartTicks = new int[0];
        int[] _lineEndTicks = new int[0];
        double[] _lineStartTimes = new double[0];
        // The change line driving each line's yaw / pitch column, or null.
        TasChange[] _yawChangeAtLine = new TasChange[0];
        TasChange[] _pitchChangeAtLine = new TasChange[0];
        // The look_at line in effect at each line, or null.
        TasFrameLine[] _lookAtAtLine = new TasFrameLine[0];
        int _totalTicks;
        double _totalTime;

        // Debug, playback only: extra ticks appended to (or taken off) one framebulk's tick count.
        // The line's own Count is never written, so saving the script keeps the authored numbers.
        int _adjustLine = -1;
        int _adjustTicks;

        /// <summary>
        /// Give one framebulk more (or fewer) ticks for the current timeline only. A norefresh
        /// resume uses it through the "bulk frames" debug dial: the resumed run's tick mapping,
        /// totals and clock include the shift, while the script and its saved bytes do not.
        /// Pass -1 / 0 to clear it.
        /// </summary>
        public void SetFrameAdjust(int lineIndex, int ticks)
        {
            if (_adjustLine == lineIndex && _adjustTicks == ticks)
                return;
            _adjustLine = lineIndex;
            _adjustTicks = ticks;
            InvalidateTimeline();
        }

        public int TotalTicks
        {
            get
            {
                EnsureTimeline();
                return _totalTicks;
            }
        }

        public double TotalTime
        {
            get
            {
                EnsureTimeline();
                return _totalTime;
            }
        }

        public void InvalidateTimeline()
        {
            _timelineDirty = true;
        }

        public void AddWarning(string warning)
        {
            if (Warnings.Count < 200)
                Warnings.Add(warning);
        }

        void EnsureTimeline()
        {
            if (!_timelineDirty)
                return;

            _timelineDirty = false;
            int lineCount = Lines.Count;
            _lineStartTicks = new int[lineCount];
            _lineEndTicks = new int[lineCount];
            _lineStartTimes = new double[lineCount];
            _yawChangeAtLine = new TasChange[lineCount];
            _pitchChangeAtLine = new TasChange[lineCount];
            _lookAtAtLine = new TasFrameLine[lineCount];

            long tickCursor = 0;
            double timeCursor = 0.0;
            TasChange yawChange = null;
            TasChange pitchChange = null;
            TasFrameLine lookAt = null;
            for (int i = 0; i < lineCount; i++)
            {
                TasFrameLine line = Lines[i];
                int count = TicksOf(line);
                // Playback-only bulk-frame dial: the framebulk a norefresh resume starts in may
                // hold more or fewer ticks for this run. Zero is allowed: it then owns no tick.
                if (i == _adjustLine)
                {
                    count += _adjustTicks;
                    if (count < 0)
                        count = 0;
                }
                double frametime = line.EffectiveFrametime(Properties.HostFrametime);
                _lineStartTicks[i] = (int)Math.Min(int.MaxValue, tickCursor);
                _lineStartTimes[i] = timeCursor;

                // A change line takes over the '-' angle column of the lines after it. A
                // line that sets that angle itself (absolute, relative or target_yaw) snaps
                // to its value and ends the change for that axis.
                if (line.Kind == TasFrameKind.ChangeYaw)
                {
                    yawChange = new TasChange
                    {
                        IsYaw = true, Final = line.Yaw.Value, Seconds = line.ChangeSeconds,
                        StartTick = _lineStartTicks[i], StartTime = timeCursor
                    };
                }
                else if (line.Kind == TasFrameKind.ChangePitch)
                {
                    pitchChange = new TasChange
                    {
                        IsYaw = false, Final = line.Pitch.Value, Seconds = line.ChangeSeconds,
                        StartTick = _lineStartTicks[i], StartTime = timeCursor
                    };
                }
                else
                {
                    if (!line.Yaw.Unchanged || line.HasTargetYaw)
                        yawChange = null;
                    if (!line.Pitch.Unchanged)
                        pitchChange = null;
                }
                _yawChangeAtLine[i] = yawChange;
                _pitchChangeAtLine[i] = pitchChange;
                // look_at stays in effect until the next look_at line ("look_at off" ends it).
                if (line.Kind == TasFrameKind.LookAt)
                    lookAt = line.LookAtEnabled ? line : null;
                _lookAtAtLine[i] = lookAt;
                tickCursor += count;
                if (tickCursor > int.MaxValue)
                {
                    AddWarning("Script exceeds the maximum supported length; timeline was clamped.");
                    tickCursor = int.MaxValue;
                }
                _lineEndTicks[i] = (int)tickCursor;
                timeCursor += count * frametime;
            }

            _totalTicks = (int)Math.Min(int.MaxValue, tickCursor);
            _totalTime = timeCursor;
            BuildCheckpoints();
            BuildNoRefreshBreaks();
            BuildBakeBlocks();
            _fastForwardTick = -1;
            for (int i = 0; i < lineCount; i++)
            {
                if (Lines[i].Kind == TasFrameKind.FastForwardEnd)
                {
                    _fastForwardTick = _lineStartTicks[i];
                    break;
                }
            }
        }

        int _fastForwardTick = -1;

        /// <summary>
        /// Start tick of the first ff line, or -1 when the script has none. Every tick
        /// below it plays uncapped; from it onward the menu playback speed applies.
        /// </summary>
        public int FastForwardTick
        {
            get
            {
                EnsureTimeline();
                return _fastForwardTick;
            }
        }

        // The absolute tick of every FFF no-refresh breakpoint, in script order.
        readonly List<int> _noRefreshBreaks = new List<int>();

        /// <summary>
        /// Every FFF breakpoint, in script order, as the absolute tick where rendering comes
        /// back. FFF takes no ticks of its own, so the breakpoint tick is the start tick of the
        /// framebulk immediately after the marker: that is the first tick shown on screen again.
        /// </summary>
        public IList<int> NoRefreshBreakTicks
        {
            get
            {
                EnsureTimeline();
                return _noRefreshBreaks;
            }
        }

        /// <summary>True when the script has at least one FFF marker.</summary>
        public bool HasNoRefreshBreaks
        {
            get
            {
                EnsureTimeline();
                return _noRefreshBreaks.Count > 0;
            }
        }

        /// <summary>
        /// The first FFF breakpoint strictly after <paramref name="tick"/>, or -1 when the
        /// cursor is already past the last marker. "Strictly" matters: the tick that owns the
        /// marker has already been reached, so re-arming there would restore rendering and
        /// suppress it again on the same tick.
        /// </summary>
        public int NextNoRefreshBreakTick(int tick)
        {
            EnsureTimeline();
            for (int i = 0; i < _noRefreshBreaks.Count; i++)
            {
                if (_noRefreshBreaks[i] > tick)
                    return _noRefreshBreaks[i];
            }
            return -1;
        }

        /// <summary>Fill <see cref="NoRefreshBreakTicks"/> from the timeline built above.</summary>
        void BuildNoRefreshBreaks()
        {
            _noRefreshBreaks.Clear();
            for (int i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Kind != TasFrameKind.NoRefreshBreak)
                    continue;
                int tick = _lineStartTicks[i];
                // A marker with nothing after it has nothing to fast-forward to.
                if (tick >= _totalTicks || tick < 0)
                    continue;
                // Two markers in a row name the same breakpoint; keep one.
                if (_noRefreshBreaks.Count > 0 && _noRefreshBreaks[_noRefreshBreaks.Count - 1] == tick)
                    continue;
                _noRefreshBreaks.Add(tick);
            }
        }

        /// <summary>The change driving this line's yaw column, or null.</summary>
        public TasChange ActiveYawChange(int lineIndex)
        {
            EnsureTimeline();
            return lineIndex >= 0 && lineIndex < _yawChangeAtLine.Length ? _yawChangeAtLine[lineIndex] : null;
        }

        /// <summary>The change driving this line's pitch column, or null.</summary>
        public TasChange ActivePitchChange(int lineIndex)
        {
            EnsureTimeline();
            return lineIndex >= 0 && lineIndex < _pitchChangeAtLine.Length ? _pitchChangeAtLine[lineIndex] : null;
        }

        /// <summary>The "look_at entity" line in effect at this line, or null when none is active.</summary>
        public TasFrameLine ActiveLookAt(int lineIndex)
        {
            EnsureTimeline();
            return lineIndex >= 0 && lineIndex < _lookAtAtLine.Length ? _lookAtAtLine[lineIndex] : null;
        }

        /// <summary>
        /// Ticks a line occupies. Checkpoints, change lines and fist/marker lines take none
        /// (a change runs alongside the framebulks after it); every other line takes at least one.
        /// </summary>
        static int TicksOf(TasFrameLine line)
        {
            if (line.Kind == TasFrameKind.Checkpoint || line.Kind == TasFrameKind.ChangeYaw ||
                line.Kind == TasFrameKind.ChangePitch || line.Kind == TasFrameKind.FastForwardEnd ||
                line.Kind == TasFrameKind.NoRefreshBreak || line.Kind == TasFrameKind.LookAt ||
                line.Kind == TasFrameKind.Fist || line.Kind == TasFrameKind.BakeStart ||
                line.Kind == TasFrameKind.BakeEnd || line.Kind == TasFrameKind.RecordVideoStart ||
                line.Kind == TasFrameKind.RecordVideoStop)
                return 0;
            return line.Count < 1 ? 1 : line.Count;
        }

        /// <summary>
        /// Index of the first line of the run of zero-tick lines (norefresh, ff, FFF, look_at,
        /// fist, bake and record_video markers, change lines) that sits directly before
        /// <paramref name="lineIndex"/>. Returns <paramref name="lineIndex"/> itself when the
        /// line has no zero-tick line before it. Playback walks this run when it reaches the
        /// first tick of a line, because a zero-tick line never owns a tick of its own.
        /// </summary>
        public int ZeroTickRunStart(int lineIndex)
        {
            if (lineIndex > Lines.Count)
                lineIndex = Lines.Count;
            int start = lineIndex;
            while (start > 0 && TicksOf(Lines[start - 1]) == 0)
                start--;
            return start;
        }

        public TickLocation Locate(int tick)
        {
            EnsureTimeline();
            TickLocation result = new TickLocation();
            result.AbsoluteTick = tick;
            if (Lines.Count == 0)
            {
                result.Valid = false;
                return result;
            }
            if (tick < 0)
                tick = 0;
            if (tick >= _totalTicks)
                tick = _totalTicks > 0 ? _totalTicks - 1 : 0;

            int lo = 0;
            int hi = _lineEndTicks.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (tick < _lineEndTicks[mid])
                    hi = mid;
                else
                    lo = mid + 1;
            }

            TasFrameLine line = Lines[lo];
            int within = tick - _lineStartTicks[lo];
            result.LineIndex = lo;
            result.TickWithinLine = within;
            result.Time = _lineStartTimes[lo] + within * line.EffectiveFrametime(Properties.HostFrametime);
            result.Valid = true;
            return result;
        }

        public double TimeAtTick(int tick)
        {
            TickLocation location = Locate(tick);
            return location.Valid ? location.Time : 0.0;
        }

        /// <summary>First absolute tick owned by Lines[lineIndex]. -1 when the index is out of range.</summary>
        public int LineStartTick(int lineIndex)
        {
            EnsureTimeline();
            if (lineIndex < 0 || lineIndex >= _lineStartTicks.Length)
                return -1;
            return _lineStartTicks[lineIndex];
        }

        /// <summary>
        /// Field 4 of the framebulk that owns the tick, inherited from
        /// host_frametime when it is '-'. This is the value the accumulator in
        /// TimeController consumes for that framebulk.
        /// </summary>
        public double FrametimeAtTick(int tick)
        {
            TickLocation location = Locate(tick);
            if (!location.Valid)
                return Properties.HostFrametime;
            return Lines[location.LineIndex].EffectiveFrametime(Properties.HostFrametime);
        }

        /// <summary>True when the tick's line carries an explicit Field 4 frametime.</summary>
        public bool TickHasFrametimeOverride(int tick)
        {
            TickLocation location = Locate(tick);
            return location.Valid && Lines[location.LineIndex].HasFrametimeOverride;
        }

        public void MergeAdjacent()
        {
            for (int i = 1; i < Lines.Count; i++)
            {
                if (Lines[i - 1].BehaviourEquals(Lines[i]))
                {
                    long merged = (long)Lines[i - 1].Count + Lines[i].Count;
                    Lines[i - 1].Count = (int)Math.Min(MaxTicksPerLine, merged);
                    Lines.RemoveAt(i);
                    i--;
                }
            }
            InvalidateTimeline();
        }

        /// <summary>
        /// Remove everything at or after a tick. If the tick falls inside a bulk
        /// line, that line is shortened. This is the primitive used by branch and
        /// undo so the script always ends exactly at the authoring point.
        /// </summary>
        public void TruncateAtTick(int tick)
        {
            if (tick < 0)
                tick = 0;
            int cursor = 0;
            for (int i = 0; i < Lines.Count; i++)
            {
                TasFrameLine line = Lines[i];
                int count = TicksOf(line);
                if (tick <= cursor)
                {
                    Lines.RemoveRange(i, Lines.Count - i);
                    InvalidateTimeline();
                    return;
                }
                if (tick < cursor + count)
                {
                    line.Count = tick - cursor;
                    if (line.Count < 1)
                        line.Count = 1;
                    if (i + 1 < Lines.Count)
                        Lines.RemoveRange(i + 1, Lines.Count - i - 1);
                    InvalidateTimeline();
                    return;
                }
                cursor += count;
            }
            InvalidateTimeline();
        }

        // ------------------------------------------------------------------
        // norefresh checkpoints
        // ------------------------------------------------------------------

        readonly List<TasCheckpoint> _checkpoints = new List<TasCheckpoint>();

        /// <summary>Every norefresh line in order, with the tick it resumes at and the checksum of everything before it.</summary>
        public IList<TasCheckpoint> Checkpoints
        {
            get
            {
                EnsureTimeline();
                return _checkpoints;
            }
        }

        /// <summary>
        /// Forget every name-based look_at pick. Playback starts and state/norefresh loads
        /// call this: the restored state can remap IDs and replace the object a pick named,
        /// so a target from the previous timeline must not be reused.
        /// </summary>
        public void ResetLookAtResolutions()
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                TasFrameLine line = Lines[i];
                if (line == null || line.LookAtName.Length == 0)
                    continue;
                line.LookAtResolvedId = 0;
                line.LookAtResolvedName = string.Empty;
                line.LookAtResolvedTick = -1;
                line.LookAtAnnounced = false;
            }
        }

        /// <summary>The checkpoint whose resume tick is exactly <paramref name="tick"/>, or null.</summary>
        public TasCheckpoint CheckpointAt(int tick)
        {
            EnsureTimeline();
            for (int i = 0; i < _checkpoints.Count; i++)
            {
                if (_checkpoints[i].Tick == tick)
                    return _checkpoints[i];
            }
            return null;
        }

        /// <summary>
        /// Checksum of the header settings and every line before each norefresh. Comments and
        /// whitespace are not part of it; a change to any input, angle, frametime or count above
        /// the marker is, and changes the checksum.
        /// </summary>
        void BuildCheckpoints()
        {
            _checkpoints.Clear();
            if (!HasCheckpointLine())
                return;

            // The prefix is hashed in its auto-bulked form (the same merging Serialize does),
            // so a script hashes identically in memory and after a save and reload.
            StringBuilder prefix = new StringBuilder(4096);
            AppendHeaderCanonical(prefix);
            TasFrameLine open = null;
            for (int i = 0; i < Lines.Count; i++)
            {
                TasFrameLine line = Lines[i];
                if (line.Kind == TasFrameKind.Checkpoint)
                {
                    string text = open == null ? prefix.ToString() : prefix.ToString() + open.ToPipeLine() + "\n";
                    _checkpoints.Add(new TasCheckpoint
                    {
                        LineIndex = i,
                        Tick = _lineStartTicks[i],
                        Label = line.CheckpointLabel,
                        Hash = HashText(text)
                    });
                }
                // ff and FFF are pacing only: adding or moving them must not invalidate a cached state.
                // Bake markers and video recording markers are not simulation either.
                if (line.Kind == TasFrameKind.FastForwardEnd || line.Kind == TasFrameKind.NoRefreshBreak ||
                    line.Kind == TasFrameKind.BakeStart || line.Kind == TasFrameKind.BakeEnd ||
                    line.Kind == TasFrameKind.RecordVideoStart || line.Kind == TasFrameKind.RecordVideoStop)
                    continue;
                if (open != null && open.BehaviourEquals(line))
                {
                    long total = (long)open.Count + line.Count;
                    open.Count = (int)Math.Min(MaxTicksPerLine, total);
                    continue;
                }
                if (open != null)
                    prefix.Append(open.ToPipeLine()).Append('\n');
                open = line.Clone();
            }
        }

        bool HasCheckpointLine()
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Kind == TasFrameKind.Checkpoint)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // bake blocks (bake_start [name] ... bake_end)
        // ------------------------------------------------------------------

        readonly List<TasBakeBlock> _bakeBlocks = new List<TasBakeBlock>();

        /// <summary>Every well-formed bake block, in script order (empty when the script has none).</summary>
        public IList<TasBakeBlock> BakeBlocks
        {
            get
            {
                EnsureTimeline();
                return _bakeBlocks;
            }
        }

        /// <summary>True when the script has at least one bake block.</summary>
        public bool HasBakeBlocks
        {
            get
            {
                EnsureTimeline();
                return _bakeBlocks.Count > 0;
            }
        }

        /// <summary>
        /// Builds the bake blocks and their checksums. Parse already rejects bad nesting; a script
        /// edited in memory into a bad shape only gets a warning and the bad block is ignored.
        ///
        /// The checksum covers the canonical header and every simulation line from the script start
        /// through the block's bake_end, in the same auto-bulked form the norefresh checksum uses (so a
        /// script hashes the same in memory and after a save). Markers and pacing lines (bake_start,
        /// bake_end, ff, FFF) are not part of it: adding, renaming or moving them invalidates nothing.
        /// Fist selection (the header and mid-script "fist" lines) is deliberately excluded too, so
        /// adding it does not force a re-bake; the live arm is read when the line runs. Any change to
        /// an input, angle, frametime, count or command before or inside the block does.
        /// </summary>
        void BuildBakeBlocks()
        {
            _bakeBlocks.Clear();
            bool any = false;
            for (int i = 0; i < Lines.Count && !any; i++)
                any = Lines[i].Kind == TasFrameKind.BakeStart || Lines[i].Kind == TasFrameKind.BakeEnd;
            if (!any)
                return;

            StringBuilder prefix = new StringBuilder(4096);
            AppendHeaderCanonical(prefix, false);
            // Older sidecars hashed the top-level fist header. Keep their hash as a fallback so
            // existing fist bakes stay usable after this change.
            StringBuilder legacyPrefix = Properties.StartFist != TasStartFist.Unchanged
                ? new StringBuilder(4096)
                : null;
            if (legacyPrefix != null)
                AppendHeaderCanonical(legacyPrefix, true);
            TasFrameLine open = null;
            TasBakeBlock current = null;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Lines.Count; i++)
            {
                TasFrameLine line = Lines[i];
                if (line.Kind == TasFrameKind.BakeStart)
                {
                    if (current != null)
                    {
                        AddWarning("bake_start at line " + line.SourceLine + " is inside another bake block; that block is ignored.");
                        current = null;
                        continue;
                    }
                    current = new TasBakeBlock
                    {
                        Index = _bakeBlocks.Count,
                        Name = line.BakeName ?? string.Empty,
                        ExcludeMapElements = line.BakeExcludeMapElements,
                        StartLineIndex = i,
                        StartTick = _lineStartTicks[i]
                    };
                    continue;
                }
                if (line.Kind == TasFrameKind.BakeEnd)
                {
                    if (current == null)
                    {
                        AddWarning("bake_end at line " + line.SourceLine + " has no bake_start; it is ignored.");
                        continue;
                    }
                    current.EndLineIndex = i;
                    current.EndTick = _lineStartTicks[i];
                    current.FileName = current.Name.Length > 0
                        ? current.Name
                        : "block" + (current.Index + 1).ToString(CultureInfo.InvariantCulture);
                    // A mode marker makes the NME block's hash distinct from the normal block's,
                    // so an existing full bake is never reused when the command is switched. Normal
                    // blocks keep their old hash exactly (the marker is only added for NME).
                    current.Hash = BakeHash(prefix, current.ExcludeMapElements, open);
                    if (legacyPrefix != null)
                        current.LegacyHash = BakeHash(legacyPrefix, current.ExcludeMapElements, open);
                    if (current.TickCount <= 0)
                        AddWarning("bake block '" + current.FileName + "' covers no ticks; it is ignored.");
                    else if (!names.Add(current.FileName))
                        AddWarning("bake block name '" + current.FileName + "' is used twice; the second block is ignored.");
                    else
                        _bakeBlocks.Add(current);
                    current = null;
                    continue;
                }
                if (line.Kind == TasFrameKind.FastForwardEnd || line.Kind == TasFrameKind.NoRefreshBreak ||
                    line.Kind == TasFrameKind.RecordVideoStart || line.Kind == TasFrameKind.RecordVideoStop ||
                    line.Kind == TasFrameKind.Fist)
                    continue;
                if (open != null && open.BehaviourEquals(line))
                {
                    long total = (long)open.Count + line.Count;
                    open.Count = (int)Math.Min(MaxTicksPerLine, total);
                    continue;
                }
                if (open != null)
                {
                    string row = open.ToPipeLine() + "\n";
                    prefix.Append(row);
                    if (legacyPrefix != null)
                        legacyPrefix.Append(row);
                }
                open = line.Clone();
            }
            if (current != null)
                AddWarning("bake_start '" + current.Name + "' is never closed with bake_end; that block is ignored.");
        }

        /// <summary>One bake block's checksum: prefix rows, the NME marker and the open row.</summary>
        static string BakeHash(StringBuilder prefix, bool excludeMapElements, TasFrameLine open)
        {
            string text = prefix.ToString();
            if (excludeMapElements)
                text += "bake_start_nme\n";
            if (open != null)
                text += open.ToPipeLine() + "\n";
            return HashText(text);
        }

        /// <summary>
        /// Parse-time check of bake_start / bake_end nesting: a bake_end without a start, a start
        /// inside an open block, a block left open at the end of the script, and two blocks with the
        /// same name (they would share one sidecar file) are errors.
        /// </summary>
        static void ValidateBakeMarkers(TasScript script, string[] sourceLines)
        {
            TasFrameLine open = null;
            int blockIndex = 0;
            Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < script.Lines.Count; i++)
            {
                TasFrameLine line = script.Lines[i];
                if (line.Kind == TasFrameKind.BakeStart)
                {
                    if (open != null)
                        throw new TasParseException(line.SourceLine, SourceText(sourceLines, line.SourceLine),
                            (line.BakeExcludeMapElements ? "bake_start_nme" : "bake_start") +
                            " inside the bake block opened at line " + open.SourceLine +
                            " (bake blocks cannot nest; close it with bake_end first)");
                    open = line;
                    blockIndex++;
                    string name = line.BakeName.Length > 0
                        ? line.BakeName
                        : "block" + blockIndex.ToString(CultureInfo.InvariantCulture);
                    int previous;
                    if (names.TryGetValue(name, out previous))
                        throw new TasParseException(line.SourceLine, SourceText(sourceLines, line.SourceLine),
                            "bake block name '" + name + "' is already used at line " + previous +
                            " (each block needs its own name: it names the .ukbake file)");
                    names[name] = line.SourceLine;
                }
                else if (line.Kind == TasFrameKind.BakeEnd)
                {
                    if (open == null)
                        throw new TasParseException(line.SourceLine, SourceText(sourceLines, line.SourceLine),
                            "bake_end without a matching bake_start or bake_start_nme");
                    open = null;
                }
            }
            if (open != null)
                throw new TasParseException(open.SourceLine, SourceText(sourceLines, open.SourceLine),
                    "bake_start or bake_start_nme is never closed: add bake_end before the end of the script");
        }

        static string SourceText(string[] sourceLines, int lineNumber)
        {
            return sourceLines != null && lineNumber >= 1 && lineNumber <= sourceLines.Length
                ? sourceLines[lineNumber - 1]
                : string.Empty;
        }

        void AppendHeaderCanonical(StringBuilder sb, bool includeStartFist = true)
        {
            TasProperties p = Properties;
            sb.Append("version ").Append(p.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("host_frametime ").Append(FormatFrametime(p.HostFrametime)).Append('\n');
            sb.Append("seed ").Append(p.Seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("start_type ").Append(FormatStartTypeText()).Append('\n');
            sb.Append("wait_for_control ").Append(p.WaitForPlayerControl ? "true" : "false").Append('\n');
            sb.Append("load_command ").Append(p.LoadCommand ?? string.Empty).Append('\n');
            // Only when set, so scripts without the header keep their existing checksums.
            // BuildBakeBlocks passes false: fist selection is not part of a bake hash.
            if (includeStartFist && p.StartFist != TasStartFist.Unchanged)
                sb.Append("fist ").Append(FormatStartFist(p.StartFist)).Append('\n');
            if (p.Difficulty != TasDifficulty.Unchanged)
                sb.Append("difficulty ").Append(FormatDifficulty(p.Difficulty)).Append('\n');
            if (p.Weapons != null)
                sb.Append("weapons ").Append(FormatWeapons(p.Weapons)).Append('\n');
            if (p.HasLoadout)
                sb.Append("loadout ").Append(FormatLoadout(p.Loadout)).Append('\n');
        }

        /// <summary>Header value of a weapons list: the keys separated by spaces, or "none" when empty.</summary>
        public static string FormatWeapons(List<string> weapons)
        {
            if (weapons == null || weapons.Count == 0)
                return "none";
            return string.Join(" ", weapons.ToArray());
        }

        /// <summary>Header value of a loadout: "revolver=iii altRevolver=ixx", fields in canonical order.</summary>
        public static string FormatLoadout(Dictionary<string, string> loadout)
        {
            if (loadout == null || loadout.Count == 0)
                return "none";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < WeaponUnlock.LoadoutFields.Length; i++)
            {
                string code;
                if (!loadout.TryGetValue(WeaponUnlock.LoadoutFields[i], out code))
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(WeaponUnlock.LoadoutFields[i]).Append('=').Append(code);
            }
            return sb.ToString();
        }

        /// <summary>Header text of a difficulty: the game's name in lower case ("violent").</summary>
        public static string FormatDifficulty(TasDifficulty difficulty)
        {
            switch (difficulty)
            {
                case TasDifficulty.Harmless: return "harmless";
                case TasDifficulty.Lenient: return "lenient";
                case TasDifficulty.Standard: return "standard";
                case TasDifficulty.Violent: return "violent";
                case TasDifficulty.Brutal: return "brutal";
                default: return "none";
            }
        }

        /// <summary>Header text of a start fist: "knuckleblaster" or "parry".</summary>
        public static string FormatStartFist(TasStartFist fist)
        {
            switch (fist)
            {
                case TasStartFist.Knuckleblaster:
                    return "knuckleblaster";
                case TasStartFist.Feedbacker:
                    return "parry";
                default:
                    return "none";
            }
        }

        string FormatStartTypeText()
        {
            return FormatStartType();
        }

        /// <summary>SHA-256 of the text, as lower-case hex.</summary>
        public static string HashText(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// SHA-256 of the canonical rows that own ticks [startTick, startTick + tickCount). The
        /// norefresh sync bake is stamped with it, so a resume only uses that bake while the frames
        /// after the marker are exactly the ones it was recorded with.
        /// </summary>
        public string HashTickRange(int startTick, int tickCount)
        {
            if (startTick < 0 || tickCount <= 0)
                return string.Empty;
            EnsureTimeline();
            int end = startTick + tickCount;
            StringBuilder sb = new StringBuilder();
            sb.Append("norefresh-sync ").Append(startTick).Append(' ').Append(tickCount);
            // Authored counts, not the timeline: the bulk-frames debug dial shifts the live
            // timeline, and it must not change which rows the stamp covers.
            int cursor = 0;
            for (int i = 0; i < Lines.Count; i++)
            {
                int lineEnd = cursor + TicksOf(Lines[i]);
                if (lineEnd > startTick && cursor < end)
                    sb.Append('\n').Append(Lines[i].ToPipeLine());
                cursor = lineEnd;
            }
            return HashText(sb.ToString());
        }

        public TasFrameLine AppendFrame(TasFrameLine line)
        {
            if (line == null)
                return null;
            if (line.Count < 1)
                line.Count = 1;
            Lines.Add(line);
            InvalidateTimeline();
            return line;
        }

        public TasFrameLine InsertAfter(int index)
        {
            TasFrameLine source = index >= 0 && index < Lines.Count ? Lines[index] : null;
            TasFrameLine copy;
            if (source != null)
            {
                copy = source.Clone();
            }
            else
            {
                copy = new TasFrameLine
                {
                    Pitch = AngleToken.Absolute(0),
                    Yaw = AngleToken.Absolute(0)
                };
            }
            copy.Count = 1;
            copy.Commands.Clear();
            if (copy.Kind == TasFrameKind.ChangeYaw || copy.Kind == TasFrameKind.ChangePitch)
                copy.ChangeSeconds = 1.0;
            int at = index < 0 ? 0 : Math.Min(index + 1, Lines.Count);
            Lines.Insert(at, copy);
            InvalidateTimeline();
            return copy;
        }

        public TasFrameLine Duplicate(int index)
        {
            if (index < 0 || index >= Lines.Count)
                return InsertAfter(Lines.Count - 1);
            TasFrameLine copy = Lines[index].Clone();
            Lines.Insert(index + 1, copy);
            InvalidateTimeline();
            return copy;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Lines.Count)
                return;
            Lines.RemoveAt(index);
            InvalidateTimeline();
        }

        /// <summary>
        /// Split one bulk line at a tick offset. The first line keeps the commands;
        /// the second starts with the same held input and no commands.
        /// </summary>
        public TasFrameLine SplitBulk(int index, int firstCount)
        {
            if (index < 0 || index >= Lines.Count)
                return null;
            TasFrameLine line = Lines[index];
            if (firstCount < 1 || firstCount >= line.Count)
                return null;
            TasFrameLine second = line.Clone();
            second.Count = line.Count - firstCount;
            second.Commands.Clear();
            line.Count = firstCount;
            Lines.Insert(index + 1, second);
            InvalidateTimeline();
            return second;
        }

        /// <summary>
        /// Splits a bulk in half. Returns false when the line has nothing to split.
        /// </summary>
        public bool SplitBulkInHalf(int index)
        {
            if (index < 0 || index >= Lines.Count)
                return false;
            int count = Lines[index].Count;
            if (count < 2)
                return false;
            return SplitBulk(index, Math.Max(1, count / 2)) != null;
        }

        // ------------------------------------------------------------------
        // Serialization
        // ------------------------------------------------------------------

        public string Serialize()
        {
            // Auto-bulk on the way to disk without touching the live document: the
            // editor, the playback cursor and the undo history all hold line indices.
            List<TasFrameLine> merged = BuildBulkedLines();
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("version " + Properties.Version.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("host_frametime " + FormatFrametime(Properties.HostFrametime));
            sb.AppendLine("seed " + Properties.Seed.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(Properties.Demo))
                sb.AppendLine("demo " + SanitizeLine(Properties.Demo));
            if (!string.IsNullOrEmpty(Properties.SaveName))
                sb.AppendLine("save " + SanitizeLine(Properties.SaveName));
            sb.AppendLine("start_type " + FormatStartType());
            if (Properties.Difficulty != TasDifficulty.Unchanged)
                sb.AppendLine("difficulty " + FormatDifficulty(Properties.Difficulty));
            if (Properties.StartFist != TasStartFist.Unchanged)
                sb.AppendLine("fist " + FormatStartFist(Properties.StartFist));
            if (Properties.Weapons != null)
                sb.AppendLine("weapons " + FormatWeapons(Properties.Weapons));
            if (Properties.HasLoadout)
                sb.AppendLine("loadout " + FormatLoadout(Properties.Loadout));
            if (!Properties.WaitForPlayerControl)
                sb.AppendLine("wait_for_control false");
            if (!string.IsNullOrEmpty(Properties.LoadCommand))
                sb.AppendLine("load_command " + SanitizeLine(Properties.LoadCommand));
            if (!string.IsNullOrEmpty(Properties.Author))
                sb.AppendLine("author " + SanitizeLine(Properties.Author));
            if (!string.IsNullOrEmpty(Properties.Description))
                sb.AppendLine("description " + SanitizeLine(Properties.Description));
            for (int i = 0; i < Properties.Extras.Count; i++)
                sb.AppendLine(Properties.Extras[i].Key + " " + SanitizeLine(Properties.Extras[i].Value));
            sb.AppendLine("frames");

            for (int i = 0; i < merged.Count; i++)
                sb.AppendLine(merged[i].ToPipeLine());

            return sb.ToString();
        }

        /// <summary>Copy of <see cref="Lines"/> with consecutive identical framebulks merged.</summary>
        List<TasFrameLine> BuildBulkedLines()
        {
            List<TasFrameLine> result = new List<TasFrameLine>(Lines.Count);
            TasFrameLine open = null;
            for (int i = 0; i < Lines.Count; i++)
            {
                TasFrameLine line = Lines[i];
                if (open != null && open.BehaviourEquals(line))
                {
                    long total = (long)open.Count + line.Count;
                    open.Count = (int)Math.Min(MaxTicksPerLine, total);
                    continue;
                }
                open = line.Clone();
                result.Add(open);
            }
            return result;
        }

        string FormatStartType()
        {
            switch (Properties.StartType)
            {
                case TasStartType.RestartMission:
                    return "restart_mission";
                case TasStartType.LoadLevel:
                    return "load_level " + SanitizeLine(Properties.LoadLevelName);
                case TasStartType.LoadState:
                    return "load_state " + Properties.LoadStateSlot.ToString(CultureInfo.InvariantCulture);
                case TasStartType.MainMenu:
                    return "main_menu";
                default:
                    return "none";
            }
        }

        /// <summary>Field 4 formatting: "0.001", "0.008", "0.01666667".</summary>
        public static string FormatFrametime(double value)
        {
            return value.ToString("0.0##########", CultureInfo.InvariantCulture);
        }

        static string SanitizeLine(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            // Atomic: write a sibling temp file, then swap it in, so a crash or a
            // concurrent reader never sees a half-written script.
            string text = Serialize();
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
            SourceName = path;
        }

        public static TasScript Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("TAS script not found: " + path, path);
            TasScript script = Parse(File.ReadAllText(path), path);
            script.SourceName = path;
            return script;
        }

        public static TasScript CreateDefault(int seed, double hostFrametime)
        {
            TasScript script = new TasScript();
            script.Properties.Seed = seed;
            script.Properties.HostFrametime = ClampFrametime(hostFrametime);
            return script;
        }

        public static double ClampFrametime(double value)
        {
            if (double.IsNaN(value) || value < MinFrametime)
                return MinFrametime;
            if (value > MaxFrametime)
                return MaxFrametime;
            return value;
        }

        // ------------------------------------------------------------------
        // Parsing
        // ------------------------------------------------------------------

        public static TasScript Parse(string text, string sourceName)
        {
            TasScript script = new TasScript();
            script.SourceName = sourceName ?? string.Empty;
            if (text == null)
                return script;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inFrames = false;

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string raw = lines[i];
                string code = StripComment(raw).Trim();
                if (code.Length == 0)
                    continue;

                if (string.Equals(code, "frames", StringComparison.OrdinalIgnoreCase))
                {
                    inFrames = true;
                    continue;
                }

                if (!inFrames)
                {
                    ParseHeader(script, code, lineNumber, raw);
                    continue;
                }

                if (code.IndexOf('|') >= 0)
                    script.Lines.Add(ParseFramebulk(script, code, lineNumber, raw));
                else
                    script.Lines.Add(ParseSpecial(script, code, lineNumber, raw));
            }

            ValidateBakeMarkers(script, lines);

            if (script.Properties.HostFrametime < MinFrametime)
                throw new TasParseException(1, string.Empty, "host_frametime must be at least " + MinFrametime);
            if (script.Lines.Count == 0)
                script.AddWarning("Script contains no framebulks.");
            script.InvalidateTimeline();
            return script;
        }

        static void ParseHeader(TasScript script, string code, int lineNumber, string raw)
        {
            // The key is the leading token only. Splitting on the first ':' or '='
            // anywhere in the line corrupted values that contain them, e.g.
            // "load_command sv_cheats=1; bxt_timer_reset" lost its command on reload.
            int keyEnd = 0;
            while (keyEnd < code.Length && code[keyEnd] != ' ' && code[keyEnd] != '\t' &&
                   code[keyEnd] != ':' && code[keyEnd] != '=')
                keyEnd++;
            if (keyEnd == 0 || keyEnd >= code.Length)
                throw new TasParseException(lineNumber, raw, "header line has no value: '" + code + "'");

            string key = code.Substring(0, keyEnd);
            int valueStart = keyEnd;
            while (valueStart < code.Length && (code[valueStart] == ' ' || code[valueStart] == '\t'))
                valueStart++;
            if (valueStart < code.Length && (code[valueStart] == ':' || code[valueStart] == '='))
                valueStart++;
            string value = code.Substring(valueStart).Trim();

            string normalized = key.ToLowerInvariant().Replace('-', '_');
            switch (normalized)
            {
                case "version":
                    int version;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) || version < 1)
                        throw new TasParseException(lineNumber, raw, "version must be a positive integer");
                    script.Properties.Version = version;
                    return;
                case "seed":
                case "rng_seed":
                case "random_seed":
                {
                    int seed;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                        throw new TasParseException(lineNumber, raw, "seed must be a 32-bit integer");
                    script.Properties.Seed = seed;
                    return;
                }
                case "host_frametime":
                case "frametime":
                case "default_frametime":
                case "ft":
                {
                    double frametime;
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out frametime) ||
                        double.IsNaN(frametime) || double.IsInfinity(frametime))
                        throw new TasParseException(lineNumber, raw, "host_frametime must be a number");
                    if (frametime < MinFrametime || frametime > MaxFrametime)
                        throw new TasParseException(lineNumber, raw,
                            "host_frametime must be between " + MinFrametime + " and " + MaxFrametime + " seconds");
                    script.Properties.HostFrametime = frametime;
                    return;
                }
                case "demo":
                    script.Properties.Demo = value;
                    return;
                case "save":
                case "savestate":
                    script.Properties.SaveName = value;
                    return;
                case "start_type":
                case "starttype":
                case "start":
                    ParseStartType(script, value, lineNumber, raw);
                    return;
                case "load":
                case "load_level":
                case "loadlevel":
                    // Short form: "load 1-4" is the same as "start_type load_level 1-4".
                    ParseStartType(script, "load_level " + value, lineNumber, raw);
                    return;
                case "load_command":
                case "loadcommand":
                    script.Properties.LoadCommand = value;
                    return;
                case "wait_for_control":
                case "wait_for_player_control":
                case "wait_for_player":
                case "waitforcontrol":
                {
                    bool wait;
                    if (!bool.TryParse(value, out wait))
                        throw new TasParseException(lineNumber, raw, "wait_for_control must be true or false");
                    script.Properties.WaitForPlayerControl = wait;
                    return;
                }
                case "fist":
                case "start_fist":
                    script.Properties.StartFist = ParseStartFist(value, lineNumber, raw);
                    return;
                case "difficulty":
                {
                    TasDifficulty difficulty;
                    if (TryParseDifficulty(value, out difficulty))
                        script.Properties.Difficulty = difficulty;
                    else
                        script.AddWarning("Line " + lineNumber + ": unknown difficulty '" + value +
                                          "' (use harmless, lenient, standard, violent, brutal or 0-4); the game's current difficulty is kept.");
                    return;
                }
                case "weapons":
                    ParseWeapons(script, value, lineNumber);
                    return;
                case "loadout":
                    ParseLoadout(script, value, lineNumber, raw);
                    return;
                case "author":
                    script.Properties.Author = value;
                    return;
                case "description":
                case "desc":
                    script.Properties.Description = value;
                    return;
                default:
                    script.Properties.Extras.Add(new KeyValuePair<string, string>(key, value));
                    script.AddWarning("Line " + lineNumber + ": preserved unknown header '" + key + "'.");
                    return;
            }
        }

        /// <summary>
        /// Parses a difficulty header value: the game's names (harmless, lenient, standard, violent,
        /// brutal), their GameDifficulty numbers 0-4, or none/current. False for anything else.
        /// </summary>
        public static bool TryParseDifficulty(string value, out TasDifficulty difficulty)
        {
            difficulty = TasDifficulty.Unchanged;
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "harmless": difficulty = TasDifficulty.Harmless; return true;
                case "lenient": difficulty = TasDifficulty.Lenient; return true;
                case "standard": difficulty = TasDifficulty.Standard; return true;
                case "violent": difficulty = TasDifficulty.Violent; return true;
                case "brutal": difficulty = TasDifficulty.Brutal; return true;
                case "none":
                case "current": return true;
            }
            int number;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) &&
                number >= (int)TasDifficulty.Harmless && number <= (int)TasDifficulty.Brutal)
            {
                difficulty = (TasDifficulty)number;
                return true;
            }
            return false;
        }

        /// <summary>
        /// "weapons rev0 sho1 arm1" (space or comma separated gear keys) or "weapons none". Unknown
        /// keys get a warning and are ignored; a value with no valid key at all is ignored whole, so
        /// a typo cannot strip every weapon. Stored deduplicated in canonical order.
        /// </summary>
        static void ParseWeapons(TasScript script, string value, int lineNumber)
        {
            string[] parts = (value ?? string.Empty).Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && parts[0].Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                script.Properties.Weapons = new List<string>();
                return;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parts.Length; i++)
            {
                string key = parts[i].ToLowerInvariant();
                if (WeaponUnlock.IsGearKey(key))
                    seen.Add(key);
                else
                    script.AddWarning("Line " + lineNumber + ": unknown weapon key '" + parts[i] + "' ignored (valid: " +
                                      string.Join(" ", WeaponUnlock.GearKeys) + ").");
            }
            if (seen.Count == 0)
            {
                script.AddWarning("Line " + lineNumber + ": weapons header has no valid key; the player's current weapons are kept.");
                return;
            }

            List<string> ordered = new List<string>(seen.Count);
            for (int i = 0; i < WeaponUnlock.GearKeys.Length; i++)
            {
                if (seen.Contains(WeaponUnlock.GearKeys[i]))
                    ordered.Add(WeaponUnlock.GearKeys[i]);
            }
            script.Properties.Weapons = ordered;
        }

        /// <summary>
        /// "loadout revolver=iii altRevolver=ixx arm=iii": one token per ForcedLoadout field, three
        /// characters for the blue, red and green variant (i IfEquipped, o ForceOn, x ForceOff).
        /// A malformed token or unknown field name is a parse error; "none" clears the loadout.
        /// </summary>
        static void ParseLoadout(TasScript script, string value, int lineNumber, string raw)
        {
            string[] tokens = (value ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1 && tokens[0].Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                script.Properties.Loadout = null;
                return;
            }

            Dictionary<string, string> loadout = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                int equals = token.IndexOf('=');
                string field;
                if (equals <= 0 || token.Length - equals - 1 != 3 ||
                    !WeaponUnlock.TryNormalizeLoadoutField(token.Substring(0, equals), out field))
                    throw new TasParseException(lineNumber, raw, "loadout token '" + token +
                        "' must look like <field>=<b><r><g> with field one of " +
                        string.Join(", ", WeaponUnlock.LoadoutFields));

                char[] code = new char[3];
                for (int c = 0; c < 3; c++)
                {
                    int option;
                    if (!WeaponUnlock.TryOptionFromChar(token[equals + 1 + c], out option))
                        throw new TasParseException(lineNumber, raw, "loadout token '" + token +
                            "': each variant must be i (IfEquipped), o (ForceOn) or x (ForceOff)");
                    code[c] = WeaponUnlock.OptionToChar(option);
                }
                loadout[field] = new string(code);
            }
            script.Properties.Loadout = loadout.Count > 0 ? loadout : null;
        }

        static TasStartFist ParseStartFist(string value, int lineNumber, string raw)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "knuckleblaster":
                case "knuckle":
                case "red":
                    return TasStartFist.Knuckleblaster;
                case "parry":
                case "feedbacker":
                case "blue":
                    return TasStartFist.Feedbacker;
                case "none":
                case "current":
                    return TasStartFist.Unchanged;
                default:
                    throw new TasParseException(lineNumber, raw, "fist must be knuckleblaster or parry");
            }
        }

        static void ParseStartType(TasScript script, string value, int lineNumber, string raw)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("current", StringComparison.OrdinalIgnoreCase))
            {
                script.Properties.StartType = TasStartType.None;
                return;
            }
            if (text.Equals("restart_mission", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("restart", StringComparison.OrdinalIgnoreCase))
            {
                script.Properties.StartType = TasStartType.RestartMission;
                return;
            }
            if (text.Equals("main_menu", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("menu", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                script.Properties.StartType = TasStartType.MainMenu;
                return;
            }
            if (text.StartsWith("load_level", StringComparison.OrdinalIgnoreCase))
            {
                string scene = text.Substring("load_level".Length).Trim();
                if (scene.Length == 0)
                    throw new TasParseException(lineNumber, raw, "load_level needs a scene name");
                script.Properties.StartType = TasStartType.LoadLevel;
                script.Properties.LoadLevelName = scene;
                return;
            }
            if (text.StartsWith("load_state", StringComparison.OrdinalIgnoreCase))
            {
                string slotText = text.Substring("load_state".Length).Trim();
                int slot;
                if (!int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) ||
                    slot < 0 || slot >= SaveStateManager.SlotCount)
                    throw new TasParseException(lineNumber, raw, "load_state slot must be 0-" + (SaveStateManager.SlotCount - 1));
                script.Properties.StartType = TasStartType.LoadState;
                script.Properties.LoadStateSlot = slot;
                return;
            }
            if (text.StartsWith("load ", StringComparison.OrdinalIgnoreCase))
            {
                string scene = text.Substring("load".Length).Trim();
                if (scene.Length == 0)
                    throw new TasParseException(lineNumber, raw, "load needs a level (e.g. load 1-4)");
                script.Properties.StartType = TasStartType.LoadLevel;
                script.Properties.LoadLevelName = scene;
                return;
            }
            throw new TasParseException(lineNumber, raw,
                "start_type must be none, restart_mission, main_menu, load_level <scene>, or load_state <slot>");
        }

        /// <summary>
        /// Parses one eight-field framebulk:
        /// autofuncs|movement|actions|frametime|yaw|pitch|frames|commands
        /// The last field is optional and may be left empty.
        /// </summary>
        static TasFrameLine ParseFramebulk(TasScript script, string code, int lineNumber, string raw)
        {
            string[] cols = code.Split('|');
            if (cols.Length < 7)
                throw new TasParseException(lineNumber, raw,
                    "framebulk needs 7 or 8 fields: autofuncs|movement|actions|frametime|yaw|pitch|frames|commands");
            if (cols.Length > 8)
                throw new TasParseException(lineNumber, raw,
                    "framebulk has " + cols.Length + " fields; the format has exactly 8 (commands may contain ';' but not '|')");

            TasFrameLine line = new TasFrameLine { SourceLine = lineNumber };

            line.Autofuncs = TasInputColumns.ParseAutofuncs(cols[0].Trim(), lineNumber, raw);
            line.Buttons = TasInputColumns.ParseMovement(cols[1].Trim(), lineNumber, raw) |
                           TasInputColumns.ParseActions(cols[2].Trim(), lineNumber, raw);

            // A framebulk that holds Slide itself never lets 'g' release it: the autofuncs only add
            // Slide, so the slam keeps the press down (held-slam landing shockwave) and the landing
            // has no fresh press for 's' to start a ground slide.
            if ((line.Autofuncs & TasAutofuncs.GroundSlam) != 0 && (line.Buttons & TasButtons.Slide) != 0)
                script.AddWarning("Line " + lineNumber + ": autofunc 'g' with Slide held by the framebulk: the held press stops the slam from releasing Slide (held-slam), and the landing gets no fresh Slide press for autoslide. Remove the movement 's' and let g/s/S press it.");

            string ftText = cols[3].Trim();
            if (ftText.Length > 0 && ftText != "-")
            {
                double frametime;
                if (!double.TryParse(ftText, NumberStyles.Float, CultureInfo.InvariantCulture, out frametime) ||
                    double.IsNaN(frametime) || double.IsInfinity(frametime))
                    throw new TasParseException(lineNumber, raw, "frametime (field 4) must be a number or '-'");
                if (frametime < MinFrametime || frametime > MaxFrametime)
                    throw new TasParseException(lineNumber, raw,
                        "frametime must be between " + MinFrametime + " and " + MaxFrametime + " seconds");
                line.HasFrametimeOverride = true;
                line.FrametimeOverride = frametime;
            }

            line.Yaw = AngleToken.Parse(cols[4].Trim(), "yaw", lineNumber, raw);
            line.Pitch = AngleToken.Parse(cols[5].Trim(), "pitch", lineNumber, raw);

            int repeats;
            if (!int.TryParse(cols[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out repeats) || repeats < 1)
                throw new TasParseException(lineNumber, raw, "frames (field 7) must be a positive integer");
            if (repeats > MaxTicksPerLine)
                throw new TasParseException(lineNumber, raw, "frames exceeds the " + MaxTicksPerLine + " per-line limit");
            line.Count = repeats;

            if (!line.Pitch.Unchanged && !line.Pitch.IsDelta &&
                (line.Pitch.Value < -89.9 || line.Pitch.Value > 89.9))
                script.AddWarning("Line " + lineNumber + ": pitch " + line.Pitch.Raw +
                                  " is outside -89.9..89.9 and will be clamped during playback.");

            if (cols.Length >= 8)
                ParseCommands(line, cols[7]);

            return line;
        }

        static TasFrameLine ParseSpecial(TasScript script, string code, int lineNumber, string raw)
        {
            string[] tokens = code.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                throw new TasParseException(lineNumber, raw, "empty statement");
            string keyword = tokens[0].ToLowerInvariant();

            switch (keyword)
            {
                case "save":
                {
                    if (tokens.Length < 2)
                        throw new TasParseException(lineNumber, raw, "save needs a name: save <name>");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.Save,
                        SaveName = tokens[1],
                        Count = 1,
                        SourceLine = lineNumber
                    };
                }
                case "seed":
                {
                    if (tokens.Length < 2)
                        throw new TasParseException(lineNumber, raw, "seed needs a number: seed <number>");
                    int seed;
                    if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                        throw new TasParseException(lineNumber, raw, "seed must be a 32-bit integer");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.Seed,
                        SeedValue = seed,
                        Count = 1,
                        SourceLine = lineNumber
                    };
                }
                case "target_yaw":
                {
                    if (tokens.Length < 2)
                        throw new TasParseException(lineNumber, raw, "target_yaw needs a value: target_yaw <value>");
                    AngleToken target = AngleToken.Parse(tokens[1], "target yaw", lineNumber, raw);
                    if (target.Unchanged || target.IsDelta)
                        throw new TasParseException(lineNumber, raw, "target_yaw needs an absolute angle");
                    TasFrameLine line = new TasFrameLine
                    {
                        Kind = TasFrameKind.TargetYaw,
                        TargetYaw = target,
                        Count = 1,
                        SourceLine = lineNumber
                    };
                    return line;
                }
                case "fist":
                {
                    // Mid-script fist selection. The header form takes none/current too; a
                    // frames-section line must name the arm it wants to switch to.
                    if (tokens.Length != 2)
                        throw new TasParseException(lineNumber, raw,
                            "fist needs one arm: fist parry or fist knuckleblaster");
                    TasStartFist target = ParseStartFist(tokens[1], lineNumber, raw);
                    if (target == TasStartFist.Unchanged)
                        throw new TasParseException(lineNumber, raw,
                            "fist in the frames section needs parry or knuckleblaster");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.Fist,
                        FistTarget = target,
                        Count = 0,
                        SourceLine = lineNumber
                    };
                }
                case "norefresh":
                {
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.Checkpoint,
                        Count = 0,
                        CheckpointLabel = tokens.Length > 1 ? string.Join(" ", tokens, 1, tokens.Length - 1) : string.Empty,
                        SourceLine = lineNumber
                    };
                }
                case "ff":
                {
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.FastForwardEnd,
                        Count = 0,
                        SourceLine = lineNumber
                    };
                }
                case "fff":
                {
                    // A standalone marker, not a framebulk: it takes no ticks and marks where
                    // the headless (no 3D rendering) fast-forward stops.
                    if (tokens.Length > 1)
                        throw new TasParseException(lineNumber, raw,
                            "FFF is a standalone marker and takes nothing else: put it alone on its line");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.NoRefreshBreak,
                        Count = 0,
                        SourceLine = lineNumber
                    };
                }
                case "change":
                    return ParseChange(script, tokens, lineNumber, raw);
                case "look_at":
                    return ParseLookAt(tokens, lineNumber, raw);
                case "bake_start":
                case "bake_start_nme":
                {
                    // bake_start [name] / bake_start_nme [name]: the name becomes part of the .ukbake
                    // file name, so it is one token of letters, digits, '-', '_' or '.'.
                    bool excludeMapElements = string.Equals(keyword, "bake_start_nme", StringComparison.OrdinalIgnoreCase);
                    string command = excludeMapElements ? "bake_start_nme" : "bake_start";
                    if (tokens.Length > 2)
                        throw new TasParseException(lineNumber, raw,
                            command + " takes at most one name: " + command + " [name]");
                    string name = tokens.Length > 1 ? tokens[1] : string.Empty;
                    for (int c = 0; c < name.Length; c++)
                    {
                        char ch = name[c];
                        if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != '.')
                            throw new TasParseException(lineNumber, raw,
                                "bake block name '" + name + "' may only use letters, digits, '-', '_' and '.'");
                    }
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.BakeStart,
                        Count = 0,
                        BakeName = name,
                        BakeExcludeMapElements = excludeMapElements,
                        SourceLine = lineNumber
                    };
                }
                case "bake_end":
                {
                    if (tokens.Length > 1)
                        throw new TasParseException(lineNumber, raw,
                            "bake_end is a standalone marker and takes nothing else");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.BakeEnd,
                        Count = 0,
                        SourceLine = lineNumber
                    };
                }
                case "record_video":
                {
                    // record_video <file>: starts a video recording here. One file token.
                    if (tokens.Length != 2)
                        throw new TasParseException(lineNumber, raw,
                            "record_video needs exactly one file name: record_video <file>");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.RecordVideoStart,
                        Count = 0,
                        RecordFile = tokens[1],
                        SourceLine = lineNumber
                    };
                }
                case "record_video_stop":
                {
                    if (tokens.Length > 1)
                        throw new TasParseException(lineNumber, raw,
                            "record_video_stop is a standalone marker and takes nothing else");
                    return new TasFrameLine
                    {
                        Kind = TasFrameKind.RecordVideoStop,
                        Count = 0,
                        SourceLine = lineNumber
                    };
                }
                default:
                    throw new TasParseException(lineNumber, raw,
                        "unknown statement '" + keyword +
                        "' (expected an 8-field framebulk, save, seed, change yaw|pitch to <v> over <t> s, target_yaw, fist, look_at, norefresh, FFF, bake_start, bake_start_nme, bake_end, record_video or record_video_stop)");
            }
        }

        /// <summary>change yaw to &lt;value&gt; over &lt;time&gt; s</summary>
        static TasFrameLine ParseChange(TasScript script, string[] tokens, int lineNumber, string raw)
        {
            // change yaw to 90 over 0.5 s
            if (tokens.Length < 6 || !tokens[1].Equals("yaw", StringComparison.OrdinalIgnoreCase) &&
                                    !tokens[1].Equals("pitch", StringComparison.OrdinalIgnoreCase))
                throw new TasParseException(lineNumber, raw,
                    "change needs 'change yaw to <value> over <time> s' or 'change pitch to <value> over <time> s'");
            if (!tokens[2].Equals("to", StringComparison.OrdinalIgnoreCase) ||
                !tokens[4].Equals("over", StringComparison.OrdinalIgnoreCase))
                throw new TasParseException(lineNumber, raw,
                    "change needs 'change yaw to <value> over <time> s'");

            bool yaw = tokens[1].Equals("yaw", StringComparison.OrdinalIgnoreCase);
            AngleToken target = AngleToken.Parse(tokens[3], yaw ? "yaw" : "pitch", lineNumber, raw);
            if (target.Unchanged || target.IsDelta)
                throw new TasParseException(lineNumber, raw, "change needs an absolute " + (yaw ? "yaw" : "pitch") + " value");

            double seconds;
            if (!double.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
                throw new TasParseException(lineNumber, raw, "change needs a time in seconds greater than 0");

            TasFrameLine line = new TasFrameLine
            {
                Kind = yaw ? TasFrameKind.ChangeYaw : TasFrameKind.ChangePitch,
                ChangeSeconds = seconds,
                SourceLine = lineNumber
            };
            if (yaw)
                line.Yaw = target;
            else
                line.Pitch = target;

            // No ticks of its own: the change drives the '-' angle column of the
            // framebulks that follow, for the given time (see TasChange).
            line.Count = 1;
            return line;
        }

        /// <summary>
        /// look_at entity &lt;id&gt; [hitbox] [ox oy oz], look_at &lt;type|category&gt; [hitbox] [ox oy oz],
        /// or look_at off. The name form aims at the nearest entity whose type name (or
        /// category) matches, for example "look_at coin 0 1 0" or "look_at v2 head 0 0 0".
        /// </summary>
        static TasFrameLine ParseLookAt(string[] tokens, int lineNumber, string raw)
        {
            TasFrameLine line = new TasFrameLine
            {
                Kind = TasFrameKind.LookAt,
                Count = 0,
                SourceLine = lineNumber
            };
            if (tokens.Length == 2 && tokens[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                return line;

            if (tokens.Length < 2)
                throw new TasParseException(lineNumber, raw,
                    "look_at needs 'look_at <type|category> [hitbox] [ox oy oz]', 'look_at entity <id> [hitbox] [ox oy oz]', 'look_at <x> <y> <z>' or 'look_at off'");

            // "look_at x y z": three plain numbers are a fixed world coordinate. An entity
            // name can never be a number (digits-only names are rejected below), so the forms
            // do not collide.
            if (tokens.Length == 4 && IsLookAtOffsetToken(tokens[1]) && IsLookAtOffsetToken(tokens[2]) &&
                IsLookAtOffsetToken(tokens[3]))
            {
                line.LookAtEnabled = true;
                line.LookAtIsPoint = true;
                line.LookAtPointX = ParseLookAtOffset(tokens[1], lineNumber, raw);
                line.LookAtPointY = ParseLookAtOffset(tokens[2], lineNumber, raw);
                line.LookAtPointZ = ParseLookAtOffset(tokens[3], lineNumber, raw);
                return line;
            }

            if (tokens[1].Equals("entity", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Length < 3)
                    throw new TasParseException(lineNumber, raw,
                        "look_at entity takes an id, an optional hitbox and an optional offset of three numbers: look_at entity <id> [hitbox] [ox oy oz]");
                int id;
                if (!int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id < 1)
                    throw new TasParseException(lineNumber, raw, "look_at entity id must be a positive integer");
                line.LookAtEnabled = true;
                line.LookAtEntity = id;
                ParseLookAtOptions(tokens, 3, line, lineNumber, raw);
                return line;
            }

            string name = tokens[1];
            if (!IsLookAtName(name))
                throw new TasParseException(lineNumber, raw,
                    "look_at name must be letters, digits, '_' or '-' (an entity type such as coin, or an enemy such as v2 or filth)");
            bool digitsOnly = true;
            for (int i = 0; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                {
                    digitsOnly = false;
                    break;
                }
            }
            if (digitsOnly)
                throw new TasParseException(lineNumber, raw,
                    "look_at name cannot be only digits; use 'look_at entity <id>' to aim at a specific ID");

            line.LookAtEnabled = true;
            line.LookAtName = name;
            ParseLookAtOptions(tokens, 2, line, lineNumber, raw);
            return line;
        }

        /// <summary>Parses the optional [hitbox] [ox oy oz] suffix after a look_at target.</summary>
        static void ParseLookAtOptions(string[] tokens, int index, TasFrameLine line, int lineNumber, string raw)
        {
            if (tokens.Length == index)
                return;

            if (IsLookAtOffsetToken(tokens[index]))
            {
                if (tokens.Length != index + 3)
                    throw new TasParseException(lineNumber, raw,
                        "look_at offset must be three numbers (world units)");
                SetLookAtOffsets(tokens, index, line, lineNumber, raw);
                return;
            }

            if (!IsLookAtName(tokens[index]))
                throw new TasParseException(lineNumber, raw,
                    "look_at hitbox must be letters, digits, '_' or '-' (for example head)");
            line.LookAtHitbox = tokens[index];
            if (tokens.Length == index + 1)
                return;
            if (tokens.Length != index + 4)
                throw new TasParseException(lineNumber, raw,
                    "look_at hitbox takes an optional offset of three numbers: look_at v2 head [ox oy oz]");
            SetLookAtOffsets(tokens, index + 1, line, lineNumber, raw);
        }

        static bool IsLookAtOffsetToken(string text)
        {
            double value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static void SetLookAtOffsets(string[] tokens, int index, TasFrameLine line, int lineNumber, string raw)
        {
            line.LookAtOffsetX = ParseLookAtOffset(tokens[index], lineNumber, raw);
            line.LookAtOffsetY = ParseLookAtOffset(tokens[index + 1], lineNumber, raw);
            line.LookAtOffsetZ = ParseLookAtOffset(tokens[index + 2], lineNumber, raw);
        }

        static bool IsLookAtName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                    return false;
            }
            return true;
        }

        static double ParseLookAtOffset(string text, int lineNumber, string raw)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value))
                throw new TasParseException(lineNumber, raw, "look_at offset must be three numbers (world units)");
            return value;
        }

        static void ParseCommands(TasFrameLine line, string commandColumn)
        {
            if (string.IsNullOrEmpty(commandColumn))
                return;
            string[] commands = commandColumn.Split(';');
            for (int i = 0; i < commands.Length; i++)
            {
                string command = commands[i].Trim();
                if (command.Length > 0)
                    line.Commands.Add(command);
            }
        }

        /// <summary>Strips a leading "once:" marker; returns true when it was present.</summary>
        public static bool SplitOncePrefix(string command, out string remainder)
        {
            remainder = (command ?? string.Empty).Trim();
            if (remainder.StartsWith("once:", StringComparison.OrdinalIgnoreCase))
            {
                remainder = remainder.Substring("once:".Length).Trim();
                return true;
            }
            return false;
        }

        /// <summary>Strip // or # comments anywhere on a line. Quotes are not special in this format.</summary>
        static string StripComment(string line)
        {
            if (line == null)
                return string.Empty;
            // Whichever marker comes first ends the line; checking '//' first let
            // "a # b // c" keep its '#' comment.
            int cut = line.Length;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '/' && line[i + 1] == '/')
                {
                    cut = i;
                    break;
                }
            }
            int hash = line.IndexOf('#');
            if (hash >= 0 && hash < cut)
                cut = hash;
            return cut < line.Length ? line.Substring(0, cut) : line;
        }
    }
}
