using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// The on-screen HUD: the info panel (anchored to any corner, scalable, themable), the
    /// BunnyMod XT style speedometer, the stacked notifications in the top-right corner and
    /// the customization tab. Everything the panel prints is read straight from the game
    /// (NewMovement, GunControl, CameraController) and reported as n/a when it is missing.
    /// </summary>
    public sealed partial class GuiManager
    {
        // ------------------------------------------------------------------
        // Colors and fonts
        // ------------------------------------------------------------------

        string _hudColorSource;
        Color _hudColor = Color.white;
        string _speedoColorSource;
        Color _speedoColor = Color.white;

        static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex))
                return fallback;
            Color color;
            return ColorUtility.TryParseHtmlString(hex.Trim(), out color) ? color : fallback;
        }

        static Color CachedColor(ref string source, ref Color cached, string current, Color fallback)
        {
            if (!string.Equals(source, current, StringComparison.Ordinal))
            {
                source = current;
                cached = ParseColor(current, fallback);
            }
            return cached;
        }

        Font _gameFont;
        Font _fallbackFont;
        float _noFontSearchUntil;

        /// <summary>Called on every scene load: the level's assets are still arriving, so no searches now.</summary>
        public void OnSceneChanged()
        {
            _noFontSearchUntil = TimeController.RealTime + 15f;
            ResetViewmodels();
            ResetHitboxes();
        }
        float _nextFontTry;
        int _fontTries;

        /// <summary>
        /// ULTRAKILL's UI font (VCR OSD Mono). It lives inside a TextMeshPro font asset, so it is
        /// located through reflection; the search repeats until the game has loaded it.
        /// </summary>
        Font GameFont()
        {
            if (_gameFont != null)
                return _gameFont;
            if (TimeController.RealTime >= _nextFontTry)
            {
                _fontTries++;
                // FindObjectsOfTypeAll walks every loaded object, so it is a real hitch. It runs
                // rarely, and never in the first seconds after a scene change.
                _nextFontTry = Math.Max(TimeController.RealTime + (_fontTries < 3 ? 5f : 60f), _noFontSearchUntil);
                _gameFont = FindGameFont();
                if (_gameFont != null)
                    _plugin.LogInfo("HUD font: using '" + _gameFont.name + "'.");
                else if (_fontTries == 1)
                    LogFontCandidates();
            }
            if (_gameFont != null)
                return _gameFont;

            // Until the game font turns up, a monospaced system font keeps the numbers steady.
            if (_fallbackFont == null)
            {
                try { _fallbackFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 16); }
                catch { }
            }
            return _fallbackFont;
        }

        Font FindGameFont()
        {
            try
            {
                Type assetType = _bridge.FindType("TMP_FontAsset");
                if (assetType != null)
                {
                    UnityEngine.Object[] assets = Resources.FindObjectsOfTypeAll(assetType);
                    for (int i = 0; i < assets.Length; i++)
                    {
                        if (assets[i].name.IndexOf("vcr", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        Font source = _bridge.GetMember(assets[i],
                            new[] { "sourceFontFile", "m_SourceFontFile_EditorRef", "m_SourceFontFile" }) as Font;
                        if (source != null)
                            return source;
                    }
                }
                Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
                for (int i = 0; i < fonts.Length; i++)
                {
                    if (fonts[i].name.IndexOf("vcr", StringComparison.OrdinalIgnoreCase) >= 0)
                        return fonts[i];
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("HUD font search failed: " + ex.Message);
            }
            return null;
        }

        void LogFontCandidates()
        {
            try
            {
                Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
                string names = string.Empty;
                for (int i = 0; i < fonts.Length && i < 20; i++)
                    names += (i > 0 ? ", " : string.Empty) + fonts[i].name;
                _plugin.LogInfo("HUD font: the game font was not found yet (fonts loaded: " +
                                (names.Length == 0 ? "none" : names) + "). Retrying; a system font is used meanwhile.");
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Styles
        // ------------------------------------------------------------------

        // The panel has its own styles: the settings window shares _hudStyle/_smallStyle, and a HUD
        // color or font change must not recolor the window.
        GUIStyle _panelStyle;
        GUIStyle _panelTitleStyle;
        GUIStyle _panelSmallStyle;
        GUIStyle _toastStyle;
        GUIStyle _speedoStyle;
        GUIStyle _speedoSubStyle;
        GUISkin _hudStylesSkin;
        readonly GUIContent _toastContent = new GUIContent();
        static float _hudAlpha = 1f;

        void EnsureHudStyles()
        {
            if (GUI.skin == _hudStylesSkin && _toastStyle != null)
                return;
            _hudStylesSkin = GUI.skin;

            _panelStyle = new GUIStyle(_hudStyle);
            _panelTitleStyle = new GUIStyle(_hudTitleStyle);
            _panelSmallStyle = new GUIStyle(_smallStyle);
            _panelSmallStyle.wordWrap = false;
            _panelStyle.clipping = TextClipping.Overflow;
            _panelStyle.alignment = TextAnchor.MiddleLeft;
            _panelStyle.wordWrap = false;
            _panelTitleStyle.clipping = TextClipping.Overflow;
            _panelTitleStyle.alignment = TextAnchor.MiddleLeft;
            _panelTitleStyle.wordWrap = false;
            _panelSmallStyle.clipping = TextClipping.Overflow;
            _panelSmallStyle.alignment = TextAnchor.MiddleLeft;

            _toastStyle = new GUIStyle(GUI.skin.label);
            _toastStyle.fontSize = 12;
            _toastStyle.wordWrap = true;
            _toastStyle.richText = false;
            _toastStyle.alignment = TextAnchor.UpperLeft;

            _speedoStyle = new GUIStyle(GUI.skin.label);
            _speedoStyle.alignment = TextAnchor.MiddleCenter;
            _speedoStyle.wordWrap = false;
            _speedoStyle.richText = false;
            _speedoStyle.clipping = TextClipping.Overflow;

            _speedoSubStyle = new GUIStyle(_speedoStyle);
        }

        // ------------------------------------------------------------------
        // Per-frame speed
        // ------------------------------------------------------------------

        Vector3 _velocity;
        bool _hasPlayerData;
        float _speedValue;
        float _speedSampleBase;
        float _speedNextSample;
        int _speedTrend;
        float _peakSpeed;
        int _lastTickSeen;
        int _speedKey = int.MinValue;
        int _secondaryKey = int.MinValue;
        string _speedText = string.Empty;
        string _speedSecondary = string.Empty;
        static readonly string[] DecimalFormats = { "F0", "F1", "F2", "F3" };

        /// <summary>
        /// Reads the player's velocity every rendered frame. Strings are rebuilt only when the
        /// displayed number changes, so a steady speed costs no allocation.
        /// </summary>
        long _fpsStamp;
        float _fpsSmoothed;
        bool _hasPlayer;
        string _playerNote = "not checked yet";

        /// <summary>The game's own level timer (StatsManager.seconds), as seconds; "n/a" without a level.</summary>
        string LevelTimerText()
        {
            float seconds;
            return _bridge.TryGetLevelTime(out seconds)
                ? seconds.ToString("0.000", CultureInfo.InvariantCulture) + "s"
                : "n/a";
        }

        /// <summary>Render fps from a monotonic timer; the TAS clock only measures it while it runs.</summary>
        void UpdateFps()
        {
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_fpsStamp != 0L)
            {
                double dt = (stamp - _fpsStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (dt > 0.00001 && dt < 1.0)
                {
                    float instant = (float)(1.0 / dt);
                    _fpsSmoothed = _fpsSmoothed <= 0f ? instant : Mathf.Lerp(_fpsSmoothed, instant, 0.05f);
                }
            }
            _fpsStamp = stamp;
        }

        // Measured playback rate: game seconds and ticks per real second, over ~1 s windows.
        long _rateStamp;
        int _rateTick;
        double _rateGameTime;
        string _rateText = string.Empty;

        /// <summary>Counts real time with the stopwatch: Time.unscaledDeltaTime follows captureDeltaTime, so it is not real time.</summary>
        void UpdateRate()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_rateStamp == 0L)
            {
                _rateStamp = now;
                _rateTick = _engine.CurrentTick;
                _rateGameTime = _engine.CurrentTime;
                return;
            }
            double seconds = (now - _rateStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (seconds < 1.0)
                return;
            double gameSeconds = _engine.CurrentTime - _rateGameTime;
            double ticks = _engine.CurrentTick - _rateTick;
            _rateText = (gameSeconds / seconds).ToString("0.000", CultureInfo.InvariantCulture) + " game s per real s,  " +
                        (ticks / seconds).ToString("0.0", CultureInfo.InvariantCulture) + " ticks/s";
            _rateStamp = now;
            _rateTick = _engine.CurrentTick;
            _rateGameTime = _engine.CurrentTime;
        }

        bool _playerLogged;
        bool _lastHasPlayer;

        /// <summary>One log line whenever the player lookup changes, so a missing player shows its cause in the BepInEx log.</summary>
        void LogPlayerLookup()
        {
            Type type = _bridge.NewMovementType;
            UnityEngine.Object direct = type != null ? UnityEngine.Object.FindObjectOfType(type) : null;
            _plugin.LogInfo("Player lookup: " + (_hasPlayer ? "found" : "NOT found") +
                            "  type: " + (type != null ? type.FullName + " (" + type.Assembly.GetName().Name + ")" : "null") +
                            "  FindObjectOfType: " + (direct != null ? direct.name : "null") +
                            "  scene: " + _bridge.SceneName +
                            "  bridge error: " + (_bridge.LastError ?? "none"));
        }

        // Weapon slot (1-based) whose key is held while the TAS is paused, 0 when none: the HUD
        // weapon line shows that slot's weapon. A change rebuilds the HUD text at once.
        int _weaponPreviewSlot;

        void UpdateWeaponPreview()
        {
            int held = 0;
            // Raw key read (the GUI-capture gate would hide it while the window has focus), but
            // not while a text field, popup or rebind can be using the digits.
            if (_input != null && _engine != null && _engine.Mode == EngineMode.Paused && !OwnsEscape)
                held = _input.ReadHeldSlotRaw();
            if (held == _weaponPreviewSlot)
                return;
            _weaponPreviewSlot = held;
            if (_cachedHudLines.Count > 0)
                BuildHudCache();
        }

        public void UpdateSpeed()
        {
            // _plugin is a MonoBehaviour that BepInEx can destroy while the tool keeps running;
            // Unity's == null would then bail out every frame, so test the reference instead.
            if (ReferenceEquals(_plugin, null) || _bridge == null)
                return;
            UpdateFps();
            UpdateRate();

            // Position, angles and vitals need only the player object; speed and velocity also
            // need its Rigidbody. Tracking them separately means one missing piece no longer
            // blanks every readout.
            _hasPlayer = _bridge.NewMovement != null;
            if (!_playerLogged || _hasPlayer != _lastHasPlayer)
            {
                _playerLogged = true;
                _lastHasPlayer = _hasPlayer;
                LogPlayerLookup();
            }
            UpdateWeaponPreview();
            Rigidbody body = _bridge.PlayerRigidbody;
            if (body == null)
            {
                _playerNote = _hasPlayer ? "player found, Rigidbody missing" : "NewMovement not found";
                _hasPlayerData = false;
                _velocity = Vector3.zero;
                _speedText = string.Empty;
                _speedKey = int.MinValue;
                return;
            }

            _hasPlayerData = true;
            _velocity = body.velocity;
            _playerNote = "rigidbody ok";
            float horizontal = Mathf.Sqrt(_velocity.x * _velocity.x + _velocity.z * _velocity.z);

            // A new run starts with a fresh peak.
            if (_engine != null && _engine.CurrentTick < _lastTickSeen)
                _peakSpeed = 0f;
            _lastTickSeen = _engine != null ? _engine.CurrentTick : 0;
            if (horizontal > _peakSpeed)
                _peakSpeed = horizontal;

            string mode = (_plugin.SpeedoMode.Value ?? "horizontal").Trim().ToLowerInvariant();
            float shown;
            if (mode == "total")
                shown = _velocity.magnitude;
            else if (mode == "vertical")
                shown = _velocity.y;
            else
                shown = horizontal;
            _speedValue = shown;

            // Rising / falling, sampled ten times a second so it does not flicker.
            float now = TimeController.RealTime;
            if (now >= _speedNextSample)
            {
                float delta = shown - _speedSampleBase;
                _speedTrend = delta > 0.3f ? 1 : (delta < -0.3f ? -1 : 0);
                _speedSampleBase = shown;
                _speedNextSample = now + 0.1f;
            }

            int decimals = Mathf.Clamp(_plugin.SpeedoDecimals.Value, 0, 3);
            float scale = decimals == 0 ? 1f : (decimals == 1 ? 10f : (decimals == 2 ? 100f : 1000f));
            int key = Mathf.RoundToInt(shown * scale) * 4 + decimals + (_plugin.SpeedoUnit.Value ? 1 << 20 : 0);
            if (key != _speedKey)
            {
                _speedKey = key;
                _speedText = (Mathf.RoundToInt(shown * scale) / scale).ToString(DecimalFormats[decimals], CultureInfo.InvariantCulture) +
                             (_plugin.SpeedoUnit.Value ? " u/s" : string.Empty);
            }

            bool showVertical = _plugin.SpeedoShowVertical.Value && mode != "vertical";
            bool showPeak = _plugin.SpeedoShowPeak.Value;
            if (!showVertical && !showPeak)
            {
                _speedSecondary = string.Empty;
                _secondaryKey = int.MinValue;
                return;
            }
            int secondary = (showVertical ? Mathf.RoundToInt(_velocity.y * 10f) : 0) * 7919 +
                            (showPeak ? Mathf.RoundToInt(_peakSpeed * 10f) : 0) + (showVertical ? 1 : 0) + (showPeak ? 2 : 0);
            if (secondary != _secondaryKey)
            {
                _secondaryKey = secondary;
                string text = string.Empty;
                if (showVertical)
                    text = "vert " + _velocity.y.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
                if (showPeak)
                    text += (text.Length > 0 ? "   " : string.Empty) + "peak " + _peakSpeed.ToString("0.0", CultureInfo.InvariantCulture);
                _speedSecondary = text;
            }
        }

        /// <summary>The BunnyMod XT style speed readout, centred on the screen by default.</summary>
        void DrawSpeedometer()
        {
            if (!_plugin.SpeedoEnabled.Value || _plugin.CleanRecordingActive || !_hasPlayerData || _speedText.Length == 0)
                return;
            if (Event.current.type != EventType.Repaint)
                return;
            EnsureHudStyles();

            float size = Mathf.Clamp(_plugin.SpeedoSize.Value, 12, 160);
            float alpha = Mathf.Clamp(_plugin.SpeedoOpacity.Value, 0.1f, 1f);

            Color color = CachedColor(ref _speedoColorSource, ref _speedoColor, _plugin.SpeedoColor.Value, Color.white);
            if (_plugin.SpeedoColorByChange.Value)
            {
                if (_speedTrend > 0)
                    color = new Color(0.35f, 1f, 0.45f, color.a);
                else if (_speedTrend < 0)
                    color = new Color(1f, 0.38f, 0.32f, color.a);
            }
            color.a *= alpha;

            float centreX = Screen.width * 0.5f + _plugin.SpeedoOffsetX.Value;
            float centreY = Screen.height * 0.5f + _plugin.SpeedoOffsetY.Value;
            // The speedometer always uses ULTRAKILL's own font, drawn from the game's glyph
            // atlas so it matches the game's HUD exactly. A system font is only a stopgap for
            // the frame or two before that atlas is located.
            if (!DrawGameFontText(centreX, centreY, _speedText, size, color, _plugin.SpeedoShadow.Value))
            {
                _speedoStyle.font = GameFont();
                _speedoStyle.fontSize = Mathf.RoundToInt(size);
                Rect main = new Rect(centreX - 400f, centreY - size * 0.7f, 800f, size * 1.4f);
                DrawOutlined(main, _speedText, _speedoStyle, color, alpha, size);
            }

            if (_speedSecondary.Length > 0)
            {
                float subSize = Mathf.Max(10f, Mathf.RoundToInt(size * 0.42f));
                Color subColor = new Color(color.r, color.g, color.b, color.a * 0.85f);
                if (!DrawGameFontText(centreX, centreY + size * 0.55f + subSize * 0.75f, _speedSecondary,
                                      subSize, subColor, _plugin.SpeedoShadow.Value))
                {
                    _speedoSubStyle.font = GameFont();
                    _speedoSubStyle.fontSize = Mathf.RoundToInt(subSize);
                    Rect sub = new Rect(centreX - 400f, centreY + size * 0.55f, 800f, subSize * 1.5f);
                    DrawOutlined(sub, _speedSecondary, _speedoSubStyle, subColor, alpha, subSize);
                }
            }
        }

        void DrawOutlined(Rect rect, string text, GUIStyle style, Color color, float alpha, float size)
        {
            Color previous = GUI.color;
            if (_plugin.SpeedoShadow.Value)
            {
                float d = Mathf.Max(1.5f, size * 0.055f);
                style.normal.textColor = new Color(0f, 0f, 0f, 0.9f * alpha);
                GUI.Label(new Rect(rect.x + d, rect.y + d, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x - d, rect.y + d, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x + d, rect.y - d, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x - d, rect.y - d, rect.width, rect.height), text, style);
            }
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
            GUI.color = previous;
        }

        // ------------------------------------------------------------------
        // HUD panel text
        // ------------------------------------------------------------------

        static string Num(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Yaw readouts wrap into [0, 360): 359.99 shows as 360.00 on the last tick before
        /// it rolls over to 0.00, so the display never drifts past a full turn or below zero.
        /// </summary>
        static string NumYaw(float value)
        {
            float wrapped = value % 360f;
            if (wrapped < 0f)
                wrapped += 360f;
            if (wrapped == 0f)
                wrapped = 0f; // normalize -0 so it never prints "-0.00"
            return wrapped.ToString("0.00", CultureInfo.InvariantCulture);
        }

        void BuildHudCache()
        {
            _cachedHudLines.Clear();
            _hudLinesVersion++;
            _cachedHudError = _engine.LastError ?? string.Empty;

            if (_engine.NoRefreshActive)
            {
                // Live feedback during the headless run: the panel (and the banner above it)
                // are the only things on screen while the 3D scene is not drawn.
                _cachedHudLines.Add("<b><color=#ffd24a>[NOREFRESH FAST-FORWARDING...]</color></b>  tick " +
                                    _engine.CurrentTick + " / " + _engine.NoRefreshTargetTick +
                                    "   " + _engine.NoRefreshTicks.ToString(CultureInfo.InvariantCulture) + " ticks in " +
                                    _engine.NoRefreshSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s  (" +
                                    _engine.NoRefreshTicksPerSecond.ToString("0", CultureInfo.InvariantCulture) + " ticks/s)");

                string checkpoint = "start";
                if (_engine.Script != null)
                {
                    IList<TasCheckpoint> marks = _engine.Script.Checkpoints;
                    for (int i = 0; i < marks.Count; i++)
                    {
                        if (marks[i].Tick > _engine.CurrentTick)
                            break;
                        checkpoint = (marks[i].Label.Length > 0 ? marks[i].Label : "norefresh") +
                                     " @" + marks[i].Tick;
                    }
                }
                _cachedHudLines.Add("no 3D rendering (" + _engine.SuspendedCameraCount + " camera(s) off)  from tick " +
                                    _engine.NoRefreshStartTick + "  room " + (_bridge.SceneName ?? "?") +
                                    "  checkpoint " + checkpoint + "  F5 / F11 aborts");

                Vector3 velocity = _velocity;
                float health;
                float hardDamage;
                _cachedHudLines.Add("speed " + Num(Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z)) +
                                    "  vel " + Num(velocity.x) + "  " + Num(velocity.y) + "  " + Num(velocity.z) +
                                    "  hp " + (_bridge.TryGetHealth(out health, out hardDamage)
                                        ? health.ToString("0", CultureInfo.InvariantCulture)
                                        : "n/a"));
            }

            if (_plugin.HudShowProgress.Value)
            {
                _cachedHudLines.Add("<b>ULTRAKILL TAS</b>  " + _cachedModeTag +
                                    (_engine.FastForward ? "  [FF]" : string.Empty) +
                                    (_engine.PlaybackSpeed < 0.999 ? "  " + _engine.PlaybackSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "x" : string.Empty));
                _cachedHudLines.Add("tick " + _engine.CurrentTick + "/" + _engine.TotalTicks +
                                    "  time " + _engine.CurrentTime.ToString("0.000", CultureInfo.InvariantCulture) + "/" +
                                    _engine.TotalTime.ToString("0.000", CultureInfo.InvariantCulture) + "s" +
                                    "  timer " + LevelTimerText());
                double frametime = _engine.LastFrametime > 0 ? _engine.LastFrametime : _time.HostFrametime;
                _cachedHudLines.Add("frametime " + frametime.ToString("0.######", CultureInfo.InvariantCulture) +
                                    " (" + (1.0 / Math.Max(0.000001, frametime)).ToString("0.#", CultureInfo.InvariantCulture) + " tps)" +
                                    "  render " + _fpsSmoothed.ToString("0", CultureInfo.InvariantCulture) + " fps" +
                                    "  enemies " + _cachedEnemyCount);
                // The game's own time scale (hitstop 0, slow motion, cutscenes); read only.
                _cachedHudLines.Add("timescale " + Time.timeScale.ToString("0.###", CultureInfo.InvariantCulture));
            }

            if (_rateText.Length > 0)
                _cachedHudLines.Add("rate " + _rateText);

            bool player = _hasPlayerData;
            bool entity = _hasPlayer;
            if (!entity)
            {
                // Title screen, loading or an intro: nothing to read. One line says why.
                _cachedHudLines.Add("player n/a  (" + (_bridge.IsMainMenu ? "title screen" : "no player in this scene") + ")");
            }
            else if (!player)
            {
                // The player is there but its Rigidbody is not: say so, rather than hiding speed silently.
                _cachedHudLines.Add("player: " + _playerNote);
            }
            if (player && _plugin.HudShowSpeed.Value)
            {
                {
                    float horizontal = Mathf.Sqrt(_velocity.x * _velocity.x + _velocity.z * _velocity.z);
                    _cachedHudLines.Add("speed " + Num(horizontal) + " u/s  (vert " +
                                        _velocity.y.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) +
                                        "  total " + Num(_velocity.magnitude) + ")");
                }
            }

            if (player && _plugin.HudShowVelocity.Value)
                _cachedHudLines.Add("vel " + Num(_velocity.x) + "  " + Num(_velocity.y) + "  " + Num(_velocity.z));

            if (entity && _plugin.HudShowPosition.Value)
            {
                Vector3 position;
                _cachedHudLines.Add(_bridge.TryGetPlayerPosition(out position)
                    ? "pos " + Num(position.x) + "  " + Num(position.y) + "  " + Num(position.z)
                    : "pos n/a");
            }

            if (entity && _plugin.HudShowAngles.Value)
            {
                float pitch;
                float yaw;
                _cachedHudLines.Add(_bridge.TryGetCameraAngles(out pitch, out yaw)
                    ? "pitch " + Num(pitch) + "  yaw " + NumYaw(yaw)
                    : "angles n/a");

                // Optimizer aid: which world axis the camera looks along, plus origin.
                Vector3 facingOrigin;
                Camera facingCamera = Camera.main;
                if (facingCamera != null && _bridge.TryGetPlayerPosition(out facingOrigin))
                {
                    float facingPitch;
                    float facingYaw;
                    if (_bridge.TryGetCameraAngles(out facingPitch, out facingYaw))
                        _cachedHudLines.Add(TasOptimizer.FacingText(facingCamera.transform.forward, facingOrigin, facingYaw, facingPitch));
                }
            }

            if (entity && _plugin.HudShowVitals.Value)
            {
                float hp = 0f;
                float hard = 0f;
                float dash = 0f;
                bool hasHp = _bridge.TryGetHealth(out hp, out hard);
                bool hasDash = _bridge.TryGetStamina(out dash);
                _cachedHudLines.Add("hp " + (hasHp ? hp.ToString("0", CultureInfo.InvariantCulture) : "n/a") +
                                    "  hard " + (hasHp ? hard.ToString("0", CultureInfo.InvariantCulture) : "n/a") +
                                    "  dash " + (hasDash
                                        ? (dash / 100f).ToString("0.0", CultureInfo.InvariantCulture) + " (" + dash.ToString("0", CultureInfo.InvariantCulture) + ")"
                                        : "n/a"));
            }

            if (entity && _plugin.HudShowState.Value)
            {
                bool onGround;
                bool sliding;
                bool dashing;
                bool jumping;
                bool slamming;
                _bridge.TryGetSlamming(out slamming);
                _cachedHudLines.Add(_bridge.TryGetMovementState(out onGround, out sliding, out dashing, out jumping)
                    ? "state " + (onGround ? "GROUND" : "AIR") + (sliding ? "  SLIDE" : string.Empty) +
                      (dashing ? "  DASH" : string.Empty) + (jumping ? "  JUMP" : string.Empty) +
                      (slamming ? "  SLAM" : string.Empty)
                    : "state n/a");
            }

            // A death is shown, not acted on: with "Pause on death (kill plane)" off the level is
            // not reloaded, and this line is where the author sees what happened.
            if (entity && _engine.DeathTick >= 0)
                _cachedHudLines.Add("<b><color=#ff4a4a>DEAD</color></b> at tick " + _engine.DeathTick +
                                    " (" + (_engine.DeathCause ?? "?") + ")" +
                                    (_plugin.PauseOnDeathKillPlane.Value ? string.Empty : "  no auto reload"));

            if (entity && _plugin.HudShowWeapon.Value)
            {
                string weapon = string.Empty;
                int slot = -1;
                int variation = -1;
                // While paused and a weapon slot key is held, show what that key would equip
                // (display only); otherwise, or when no preview is available, the real weapon.
                bool previewed = _weaponPreviewSlot > 0 && _engine.Mode == EngineMode.Paused &&
                                 _bridge.TryPreviewWeaponSlot(_weaponPreviewSlot, out weapon, out slot, out variation);
                _cachedHudLines.Add(previewed || _bridge.TryGetWeapon(out weapon, out slot, out variation)
                    ? "weapon " + (weapon.Length > 0 ? weapon : "none") + "  (slot " + slot + ", variation " + variation + ")"
                    : "weapon n/a");
            }

            if (_plugin.HudShowInputs.Value)
                _cachedHudLines.Add("input " + TasInputColumns.FormatCompact(_input != null ? _input.LiveButtons : TasButtons.None));

            if (_engine.FreeLookActive || _engine.Mode == EngineMode.Paused)
            {
                _cachedHudLines.Add("<color=#7fd3ff>STAGED</color> " + TasInputColumns.FormatCompact(_engine.StagedButtons) +
                                    "  pitch/yaw " + Num(_engine.StagedPitch) + "/" + NumYaw(_engine.StagedYaw));
                // The same state written as one framebulk line, in the .uktas field order:
                // autofuncs|movement|actions|frametime|yaw|pitch|frames
                _cachedHudLines.Add("<color=#7fd3ff>BULK</color> " + TasInputColumns.NeutralAutofuncs + "|" +
                                    TasInputColumns.FormatMovement(_engine.StagedButtons) + "|" +
                                    TasInputColumns.FormatActions(_engine.StagedButtons) + "|" +
                                    _time.HostFrametime.ToString("0.0##########", CultureInfo.InvariantCulture) + "|" +
                                    NumYaw(_engine.StagedYaw) + "|" + Num(_engine.StagedPitch) + "|1");
            }

            if (_plugin.HudShowRng.Value)
                _cachedHudLines.Add("seed " + _rng.CurrentSeed + "  RNG " + (_rng.SystemRandomPatched ? "deterministic" : "unity-only"));

            if (_plugin.HudShowLevelStats.Value)
                AppendLevelStats();

            if (entity && _plugin.HudEntityInspector.Value)
                AppendEntityInspector();

            if (entity && _plugin.HudShowHit.Value)
            {
                Vector3 hitPoint;
                _cachedHudLines.Add(TryCrosshairHit(out hitPoint)
                    ? "hit " + Num(hitPoint.x) + " " + Num(hitPoint.y) + " " + Num(hitPoint.z)
                    : "hit none");
            }
        }

        // The game's rank colors (StatsManager.GetRanks / GetFinalRank).
        static string RankText(string letter)
        {
            string color;
            switch (letter)
            {
                case "D": color = "#0094FF"; break;
                case "C": color = "#4CFF00"; break;
                case "B": color = "#FFD800"; break;
                case "A": color = "#FF6A00"; break;
                case "S": color = "#FF0000"; break;
                case "P": color = "#FFFFFF"; break;
                default: return letter;
            }
            return "<b><color=" + color + ">" + letter + "</color></b>";
        }

        static string StatsClock(float seconds)
        {
            int minutes = (int)(seconds / 60f);
            return minutes.ToString(CultureInfo.InvariantCulture) + ":" +
                   (seconds - minutes * 60f).ToString("00.000", CultureInfo.InvariantCulture);
        }

        /// <summary>"P: 52" until the category is an S, then "P ok". The P rank needs an S in kills, score and time.</summary>
        static string NeedForP(string rank, string needed)
        {
            if (rank == "S")
                return "  P ok";
            return needed.Length > 0 ? "  P: " + needed : string.Empty;
        }

        /// <summary>
        /// Four compact lines read from the game's level stats (see GameBridge.TryGetLevelStats):
        /// kills, score and time with their ranks and what an S (and so a P) needs, then the
        /// total rank, restarts and the challenge. "--" when there is no level.
        /// </summary>
        void AppendLevelStats()
        {
            GameBridge.LevelStatsInfo stats;
            if (!_bridge.TryGetLevelStats(out stats))
            {
                _cachedHudLines.Add("kills --  score --  time --");
                _cachedHudLines.Add("rank --  challenge --");
                return;
            }

            _cachedHudLines.Add("kills " + stats.Kills.ToString(CultureInfo.InvariantCulture) + " " + RankText(stats.KillsRank) +
                                NeedForP(stats.KillsRank, stats.KillsForS >= 0 ? stats.KillsForS.ToString(CultureInfo.InvariantCulture) : string.Empty));
            _cachedHudLines.Add("score " + stats.Score.ToString(CultureInfo.InvariantCulture) + " " + RankText(stats.ScoreRank) +
                                NeedForP(stats.ScoreRank, stats.ScoreForS >= 0 ? stats.ScoreForS.ToString(CultureInfo.InvariantCulture) : string.Empty));
            _cachedHudLines.Add("time " + StatsClock(stats.Seconds) + " " + RankText(stats.TimeRank) +
                                NeedForP(stats.TimeRank, stats.TimeForS >= 0 ? "max " + StatsClock(stats.TimeForS) : string.Empty));

            string challenge = stats.Challenge < 0 ? "--" : stats.Challenge == 1 ? "YES" : stats.Challenge == 2 ? "FAILED" : "NO";
            _cachedHudLines.Add("rank " + RankText(stats.TotalRank) + (stats.EndScreen ? " (final)" : string.Empty) +
                                "  restarts " + stats.Restarts.ToString(CultureInfo.InvariantCulture) +
                                (stats.Restarts > 0 ? " (no P)" : string.Empty) +
                                "  challenge " + challenge);
        }

        const float HitRange = 1000f;
        readonly RaycastHit[] _crosshairHits = new RaycastHit[32];

        /// <summary>
        /// The nearest solid surface on the camera's forward ray: Physics.Raycast over every
        /// layer, trigger volumes ignored, the player's own colliders skipped. False when the
        /// ray hits nothing within HitRange or there is no camera.
        /// </summary>
        bool TryCrosshairHit(out Vector3 point)
        {
            point = Vector3.zero;
            Transform camera;
            if (!_bridge.TryGetCameraTransform(out camera) || camera == null)
                return false;
            Transform player = _bridge.PlayerTransform;
            try
            {
                int count = Physics.RaycastNonAlloc(camera.position, camera.forward, _crosshairHits, HitRange,
                                                    ~0, QueryTriggerInteraction.Ignore);
                float nearest = float.MaxValue;
                bool found = false;
                for (int i = 0; i < count && i < _crosshairHits.Length; i++)
                {
                    Collider collider = _crosshairHits[i].collider;
                    if (collider == null || (player != null && collider.transform.IsChildOf(player)))
                        continue;
                    if (_crosshairHits[i].distance < nearest)
                    {
                        nearest = _crosshairHits[i].distance;
                        point = _crosshairHits[i].point;
                        found = true;
                    }
                }
                return found;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Copies the surface point under the crosshair as "x y z" (3 decimals).</summary>
        void CopyHitCoordinate()
        {
            Vector3 point;
            if (!TryCrosshairHit(out point))
            {
                _statusText = "No surface under the crosshair: nothing copied.";
                _statusIsError = true;
                _plugin.Toast("Hit: no surface under the crosshair.", true);
                return;
            }
            string text = point.x.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                          point.y.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                          point.z.ToString("0.000", CultureInfo.InvariantCulture);
            GUIUtility.systemCopyBuffer = text;
            _statusText = "Copied hit coordinate: " + text;
            _statusIsError = false;
            _plugin.Toast("Copied hit coordinate: " + text, false);
        }

        const float InspectorRange = 1000f;

        /// <summary>
        /// Entity inspector: the entity on the camera's forward ray. Built with the rest of the
        /// HUD text on its timer, so the ray and the strings cost the same as the other lines.
        /// </summary>
        void AppendEntityInspector()
        {
            Transform camera;
            if (!_bridge.TryGetCameraTransform(out camera))
            {
                _cachedHudLines.Add("entity n/a (no camera)");
                return;
            }
            EntityRegistry registry = _bridge.Entities;
            float hitDistance;
            bool occluded;
            EntityRegistry.Entity picked = registry.Pick(camera.position, camera.forward, InspectorRange,
                                                         out hitDistance, out occluded);
            if (picked == null || !GameBridge.IsAlive(picked.Component))
            {
                _cachedHudLines.Add("entity none in view  (" + registry.LevelCount + " level IDs)");
                return;
            }

            Component component = picked.Component;
            Vector3 position = component.transform.position;
            _cachedHudLines.Add("<b>entity " + picked.Id + "</b>  " + picked.Category + "  " + picked.TypeName +
                                "  '" + component.gameObject.name + "'" +
                                (picked.Spawned ? "  (spawned)" : string.Empty) +
                                (occluded ? "  [behind geometry]" : string.Empty));
            _cachedHudLines.Add("  pos " + Num(position.x) + "  " + Num(position.y) + "  " + Num(position.z) +
                                "  dist " + Num(Vector3.Distance(camera.position, position)) +
                                "  (ray " + Num(hitDistance) + ")");
            string state = registry.DescribeState(picked);
            _cachedHudLines.Add("  " + (state.Length > 0 ? state : "state n/a"));
            Vector3 velocity;
            _cachedHudLines.Add(registry.TryGetVelocity(picked, out velocity)
                ? "  vel " + Num(velocity.x) + "  " + Num(velocity.y) + "  " + Num(velocity.z) + "  (" + Num(velocity.magnitude) + ")"
                : "  vel n/a (no Rigidbody)");
        }

        // ------------------------------------------------------------------
        // HUD panel drawing
        // ------------------------------------------------------------------

        const float HudTextMinWidth = 300f;
        const float HudInputWidth = 560f;

        // Row height follows the font (the game font is taller than the default one); fixed
        // 14 px rows clipped the tops of its glyphs.
        float _panelLineHeight = 17f;
        float _measuredTextWidth = HudTextMinWidth;
        int _measuredVersion = -1;
        Font _measuredFont;
        int _hudLinesVersion;
        readonly GUIContent _measureContent = new GUIContent();

        void DrawHud()
        {
            if (_cachedHudLines.Count == 0)
                BuildHudCache();
            EnsureHudStyles();

            bool inputDisplay = _plugin.HudShowInputDisplay.Value;
            bool staged = inputDisplay && (_engine.Mode == EngineMode.Paused || _engine.FreeLookActive);
            int matrixRows = 0;
            if (inputDisplay)
                matrixRows = 2 + (_plugin.HudShowPointer.Value ? 1 : 0) + (staged ? 2 : 0);
            bool hasError = !string.IsNullOrEmpty(_cachedHudError) && _plugin.HudShowProgress.Value;
            if (_cachedHudLines.Count == 0 && matrixRows == 0 && !hasError)
                return;
            if (!_loggedHudDraw)
            {
                _loggedHudDraw = true;
                _plugin.LogInfo("HUD drawing with " + _cachedHudLines.Count + " lines.");
            }

            // Fonts and styles first: row height and width depend on them.
            Color text = CachedColor(ref _hudColorSource, ref _hudColor, _plugin.HudColor.Value, Color.white);
            text.a *= Mathf.Clamp(_plugin.HudOpacity.Value, 0.1f, 1f);
            Font font = _plugin.HudGameFont.Value ? GameFont() : null;
            _panelStyle.font = font;
            _panelTitleStyle.font = font;
            _panelSmallStyle.font = font;
            _matrixStyle.font = font;
            _panelStyle.normal.textColor = text;
            _panelTitleStyle.normal.textColor = text;
            _panelSmallStyle.normal.textColor = text;

            // Re-measure only when the lines or the font changed (CalcSize is not free).
            if (_measuredVersion != _hudLinesVersion || !ReferenceEquals(_measuredFont, font))
            {
                _measuredVersion = _hudLinesVersion;
                _measuredFont = font;
                float widest = HudTextMinWidth;
                for (int i = 0; i < _cachedHudLines.Count; i++)
                {
                    _measureContent.text = _cachedHudLines[i];
                    float w = (i == 0 ? _panelTitleStyle : _panelStyle).CalcSize(_measureContent).x;
                    if (w > widest)
                        widest = w;
                }
                _measuredTextWidth = widest + 24f;
                _measureContent.text = "Ag";
                _panelLineHeight = Mathf.Max(16f, Mathf.Ceil(_panelStyle.CalcSize(_measureContent).y) + 2f);
            }

            float textWidth = _measuredTextWidth;
            float width = inputDisplay ? Mathf.Max(HudInputWidth, textWidth) : textWidth;
            float textHeight = _cachedHudLines.Count > 0 ? _panelLineHeight * _cachedHudLines.Count + 12f : 0f;
            float height = textHeight + (matrixRows > 0 ? 4f + matrixRows * 20f : 0f) + (hasError ? 28f : 0f);

            float scale = Mathf.Clamp(_plugin.HudScale.Value, 0.5f, 3f);
            float alpha = Mathf.Clamp(_plugin.HudOpacity.Value, 0.1f, 1f);
            float offsetX = _plugin.HudOffsetX.Value;
            float offsetY = _plugin.HudOffsetY.Value;
            string corner = (_plugin.HudCorner.Value ?? "topleft").Trim().ToLowerInvariant();
            float x = corner.EndsWith("right", StringComparison.Ordinal) ? Screen.width - width * scale - offsetX : offsetX;
            float y = corner.StartsWith("bottom", StringComparison.Ordinal) ? Screen.height - height * scale - offsetY : offsetY;

            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            GUI.matrix = Matrix4x4.TRS(new Vector3(x, y, 0f), Quaternion.identity, new Vector3(scale, scale, 1f));
            _hudAlpha = alpha;

            float cursor = 0f;
            if (_cachedHudLines.Count > 0)
            {
                if (_plugin.HudBackground.Value)
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.Box(new Rect(0f, 0f, textWidth, textHeight), GUIContent.none);
                }
                float lineY = 6f;
                for (int i = 0; i < _cachedHudLines.Count; i++)
                {
                    GUI.Label(new Rect(8f, lineY, textWidth - 12f, _panelLineHeight), _cachedHudLines[i],
                        i == 0 ? _panelTitleStyle : _panelStyle);
                    lineY += _panelLineHeight;
                }
                cursor = textHeight + 4f;
            }

            if (matrixRows > 0)
            {
                // "now": everything held on the devices this frame (your keys, or the keys the
                // script injects) plus the last scripted tick while it plays. CurrentButtons is
                // the exact mask injected on this tick, so macro presses such as G1's variation
                // re-equip light up too. "F6": what the next frame advance will commit while
                // the clock is frozen.
                TasButtons now = _input.LiveButtons;
                if (_engine.Mode == EngineMode.Playing)
                    now |= _engine.LastButtons | _input.CurrentButtons;
                Color green = new Color(0.35f, 1f, 0.55f);
                DrawMatrixRow(0f, cursor, "now", RowMove, now, green);
                cursor += 20f;
                DrawMatrixRow(0f, cursor, "now", RowWeapons, now, green);
                cursor += 20f;
                if (_plugin.HudShowPointer.Value)
                {
                    DrawPointerRow(0f, cursor, green);
                    cursor += 20f;
                }
                if (staged)
                {
                    Color cyan = new Color(0.45f, 0.85f, 1f);
                    DrawMatrixRow(0f, cursor, "F6", RowMove, _engine.StagedButtons, cyan);
                    cursor += 20f;
                    DrawMatrixRow(0f, cursor, "F6", RowWeapons, _engine.StagedButtons, cyan);
                    cursor += 20f;
                }
            }

            if (hasError)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Box(new Rect(0f, cursor + 2f, width, 22f), GUIContent.none);
                GUI.Label(new Rect(6f, cursor + 4f, width - 12f, 20f), _cachedHudError, _errorStyle);
            }

            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }

        /// <summary>
        /// The NoRefresh banner, drawn across the top of the screen. It is shown even when the
        /// info panel is switched off (F12): with no 3D rendering this is the only feedback that
        /// the simulation is still advancing, where it is going, and how to abort it.
        /// </summary>
        void DrawNoRefreshBanner()
        {
            if (!_engine.RenderSuppressed || _plugin.CleanRecordingActive)
                return;
            // Pure drawing: one Repaint per frame is enough. Layout and input events cost the
            // same IMGUI work for a screen that is not being refreshed.
            if (Event.current.type != EventType.Repaint)
                return;
            EnsureHudStyles();

            if (_noRefreshStyle == null)
            {
                _noRefreshStyle = new GUIStyle(GUI.skin.label);
                _noRefreshStyle.alignment = TextAnchor.MiddleCenter;
            }
            Font font = _plugin.HudGameFont.Value ? GameFont() : null;
            _noRefreshStyle.font = font;
            _noRefreshStyle.fontSize = 16;
            _noRefreshStyle.normal.textColor = new Color(1f, 0.83f, 0.29f, 1f);

            string text = "[NOREFRESH FAST-FORWARDING...]   tick " + _engine.CurrentTick +
                          " / " + _engine.NoRefreshTargetTick +
                          "   " + _engine.NoRefreshTicks.ToString(CultureInfo.InvariantCulture) + " ticks" +
                          "   " + _engine.NoRefreshSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s" +
                          "   " + _engine.NoRefreshTicksPerSecond.ToString("0", CultureInfo.InvariantCulture) + " ticks/s" +
                          "   (F5 / F11 aborts)";

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Box(new Rect(0f, 0f, Screen.width, 30f), GUIContent.none);
            GUI.color = previous;
            GUI.Label(new Rect(0f, 5f, Screen.width, 22f), text, _noRefreshStyle);
        }

        // ------------------------------------------------------------------
        // Notifications, top-right
        // ------------------------------------------------------------------

        const float ToastSeconds = 5f;
        const float ToastErrorSeconds = 8f;

        /// <summary>
        /// Stacked notifications under the top-right corner. The newest is on top, each is sized to
        /// its text, errors are tinted red, and they fade out instead of overlapping or vanishing.
        /// </summary>
        void DrawToast()
        {
            if (Event.current.type != EventType.Repaint || _plugin.CleanRecordingActive)
                return;
            List<TasMessage> messages = _plugin.Messages;
            if (messages.Count == 0)
                return;
            EnsureHudStyles();

            float now = TimeController.RealTime;
            float width = Mathf.Min(520f, Screen.width * 0.45f);
            float x = Screen.width - width - 16f;
            float y = 16f;
            int shown = 0;
            Color oldColor = GUI.color;

            for (int i = messages.Count - 1; i >= 0 && shown < 5; i--)
            {
                TasMessage message = messages[i];
                float life = message.IsError ? ToastErrorSeconds : ToastSeconds;
                float age = now - message.Time;
                if (age > life)
                    continue;
                float alpha = age > life - 1f ? Mathf.Clamp01(life - age) : 1f;

                _toastContent.text = message.Text;
                float height = _toastStyle.CalcHeight(_toastContent, width - 16f) + 10f;
                Rect box = new Rect(x, y, width, height);

                GUI.color = message.IsError
                    ? new Color(1f, 0.55f, 0.5f, 0.95f * alpha)
                    : new Color(1f, 1f, 1f, 0.92f * alpha);
                GUI.Box(box, GUIContent.none);
                _toastStyle.normal.textColor = message.IsError
                    ? new Color(1f, 0.82f, 0.8f, alpha)
                    : new Color(1f, 1f, 1f, alpha);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + 8f, y + 4f, width - 16f, height - 6f), message.Text, _toastStyle);

                y += height + 4f;
                shown++;
            }
            GUI.color = oldColor;
        }

        // ------------------------------------------------------------------
        // The Overlays tab
        // ------------------------------------------------------------------

        static readonly string[] CornerValues = { "topleft", "topright", "bottomleft", "bottomright" };
        static readonly string[] CornerLabels = { "Top left", "Top right", "Bottom left", "Bottom right" };
        static readonly string[] CornerTips =
        {
            "Anchor the info panel to the top left corner.",
            "Anchor the info panel to the top right corner.",
            "Anchor the info panel to the bottom left corner.",
            "Anchor the info panel to the bottom right corner."
        };
        static readonly string[] SpeedoModeValues = { "horizontal", "total", "vertical" };
        static readonly string[] SpeedoModeLabels = { "Horizontal (XZ)", "Total (3D)", "Vertical (Y)" };
        static readonly string[] SpeedoModeTips =
        {
            "Speed along the ground plane only. What most speedrun tools show.",
            "Full 3D speed, including falling and rising.",
            "Only the up / down speed."
        };

        static int IndexOf(string[] values, string current)
        {
            string key = (current ?? string.Empty).Trim().ToLowerInvariant();
            for (int i = 0; i < values.Length; i++)
                if (values[i] == key)
                    return i;
            return 0;
        }

        void SliderRow(string label, BepInEx.Configuration.ConfigEntry<float> entry, float min, float max, string tip)
        {
            GUILayout.BeginHorizontal();
            Lbl(label, tip, GUILayout.Width(150));
            float value = Slide(entry.Value, min, max, tip, GUILayout.Width(220));
            GUILayout.Label(value.ToString("0.00", CultureInfo.InvariantCulture), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (Math.Abs(value - entry.Value) > 0.0001f)
                entry.Value = Mathf.Round(value * 100f) / 100f;
        }

        void IntSliderRow(string label, BepInEx.Configuration.ConfigEntry<int> entry, int min, int max, string tip)
        {
            GUILayout.BeginHorizontal();
            Lbl(label, tip, GUILayout.Width(150));
            int value = Mathf.RoundToInt(Slide(entry.Value, min, max, tip, GUILayout.Width(220)));
            GUILayout.Label(value.ToString(CultureInfo.InvariantCulture), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (value != entry.Value)
                entry.Value = value;
        }

        void OffsetRow(string label, BepInEx.Configuration.ConfigEntry<int> x, BepInEx.Configuration.ConfigEntry<int> y, string tip)
        {
            GUILayout.BeginHorizontal();
            Lbl(label, tip, GUILayout.Width(150));
            GUILayout.Label("X", GUILayout.Width(14));
            x.Value = Mathf.Clamp(IntField(x.Value, 56, tip), -4000, 4000);
            GUILayout.Label("Y", GUILayout.Width(14));
            y.Value = Mathf.Clamp(IntField(y.Value, 56, tip), -4000, 4000);
            GUILayout.EndHorizontal();
        }

        void ColorRow(string label, BepInEx.Configuration.ConfigEntry<string> entry, string tip)
        {
            GUILayout.BeginHorizontal();
            Lbl(label, tip, GUILayout.Width(150));
            entry.Value = GUILayout.TextField(entry.Value ?? string.Empty, 9, GUILayout.Width(90));
            Tip(tip);
            Color previous = GUI.color;
            GUI.color = ParseColor(entry.Value, Color.white);
            GUILayout.Box(GUIContent.none, GUILayout.Width(40), GUILayout.Height(18));
            GUI.color = previous;
            Hint("#RRGGBB");
            GUILayout.EndHorizontal();
        }

        void DrawOverlays()
        {
            BeginCard("HUD panel");
            _plugin.ShowHud.Value = Tog(_plugin.ShowHud.Value, "Show the HUD panel (F12)",
                "The info panel with tick, speed, position and inputs.");
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(400));
            _plugin.HudShowProgress.Value = Tog(_plugin.HudShowProgress.Value, "Mode, tick, time, frametime, fps", "Playback mode, current tick, elapsed time, tick length and render fps.");
            _plugin.HudShowSpeed.Value = Tog(_plugin.HudShowSpeed.Value, "Speed (horizontal / vertical / total)", "Player speed in units per second.");
            _plugin.HudShowVelocity.Value = Tog(_plugin.HudShowVelocity.Value, "Velocity vector (X, Y, Z)", "The three components of the player's velocity.");
            _plugin.HudShowPosition.Value = Tog(_plugin.HudShowPosition.Value, "Position (X, Y, Z)", "Where the player is in the level.");
            _plugin.HudShowAngles.Value = Tog(_plugin.HudShowAngles.Value, "Camera pitch / yaw", "Where the camera is looking.");
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            _plugin.HudShowVitals.Value = Tog(_plugin.HudShowVitals.Value, "Health, hard damage, dash charge", "HP, hard damage and dash charge (stamina bars).");
            _plugin.HudShowState.Value = Tog(_plugin.HudShowState.Value, "Ground / slide / dash / jump state", "Whether V1 is grounded, sliding, dashing or jumping.");
            _plugin.HudShowWeapon.Value = Tog(_plugin.HudShowWeapon.Value, "Equipped weapon", "Current weapon, slot and variation.");
            _plugin.HudShowInputs.Value = Tog(_plugin.HudShowInputs.Value, "Held buttons as text", "The buttons held this tick, written as text.");
            _plugin.HudShowRng.Value = Tog(_plugin.HudShowRng.Value, "RNG seed and state", "The random seed and whether randomness is deterministic.");
            _plugin.HudEntityInspector.Value = Tog(_plugin.HudEntityInspector.Value, "Entity inspector (look_at ID under crosshair)", "Show the ID, type and state of the enemy or object under the crosshair.");
            _plugin.HudShowHit.Value = Tog(_plugin.HudShowHit.Value, "Surface hit coordinate under crosshair", "Show \"hit x y z\" for the surface the crosshair points at, or \"hit none\".");
            _plugin.HudShowLevelStats.Value = Tog(_plugin.HudShowLevelStats.Value, "Level stats (kills, score, time, ranks, challenge)", "Live kills, score and time with their ranks, the total rank and the challenge, plus what each needs for an S (a P rank needs all three S). Shows -- outside a level.");
            GUILayout.BeginHorizontal();
            if (Btn("Copy hit coordinate", "Copy the surface point under the crosshair to the clipboard as \"x y z\" (3 decimals). Shows a message if there is no hit.", GUILayout.Width(170)))
                CopyHitCoordinate();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            EndCard();

            BeginCard("Visuals");
            _plugin.HideViewmodels.Value = Tog(_plugin.HideViewmodels.Value,
                "Hide first-person viewmodels (weapons and arms; visual only)",
                "Do not draw the weapon and arm models in front of the camera. Gameplay is unaffected.");
            _plugin.ShowHitboxes.Value = Tog(_plugin.ShowHitboxes.Value,
                "Show enemy hitboxes", "Outline the collision boxes of nearby enemies.");
            _plugin.ShowPlayerHitbox.Value = Tog(_plugin.ShowPlayerHitbox.Value,
                "Show local player hitbox (collision bounds)", "Outline V1's own collision shape.");
            bool freecam = _plugin.FreecamActive;
            bool freecamWanted = Tog(freecam,
                "Freecam (" + _plugin.KeyToggleFreecam.Value + "): WASD, Space up, Q down; Shift fast, Ctrl slow",
                "Fly a free camera around the level without moving V1.");
            if (freecamWanted != freecam)
                _plugin.SetFreecam(freecamWanted);
            GUILayout.BeginHorizontal();
            Lbl("Freecam speed", "How fast the freecam flies, in units per second.", GUILayout.Width(150));
            float freecamSpeed = Slide(_plugin.FreecamSpeed.Value, 1f, 100f, "Freecam speed.", GUILayout.Width(180));
            GUILayout.Label(freecamSpeed.ToString("0", CultureInfo.InvariantCulture) + " u/s", GUILayout.Width(60));
            GUILayout.EndHorizontal();
            if (Math.Abs(freecamSpeed - _plugin.FreecamSpeed.Value) > 0.5f)
                _plugin.FreecamSpeed.Value = Mathf.Round(freecamSpeed);
            _plugin.FreecamShowV1Body.Value = Tog(_plugin.FreecamShowV1Body.Value,
                "Show V1 playermodel in freecam",
                "While freecam is on, show V1's full body renderers. Restored when freecam ends.");

            GUILayout.BeginHorizontal();
            _plugin.ShowTriggers.Value = Tog(_plugin.ShowTriggers.Value, "Show triggers (level trigger / event volumes)",
                "Outline the level's trigger volumes with their names. Read only.", GUILayout.Width(330));
            Lbl("within", "Only triggers closer than this are drawn.", GUILayout.Width(44));
            float triggerDistance = Slide(_plugin.ShowTriggersDistance.Value, 5f, 500f, "Trigger draw distance.", GUILayout.Width(160));
            GUILayout.Label(triggerDistance.ToString("0", CultureInfo.InvariantCulture) + " u", GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (Math.Abs(triggerDistance - _plugin.ShowTriggersDistance.Value) > 0.5f)
                _plugin.ShowTriggersDistance.Value = Mathf.Round(triggerDistance);
            GUILayout.BeginHorizontal();
            _plugin.ShowTriggersSolid.Value = Tog(_plugin.ShowTriggersSolid.Value, "Solid boxes",
                "Fill each trigger volume instead of drawing only its outline.", GUILayout.Width(110));
            Lbl("opacity", "How opaque the solid trigger boxes are.", GUILayout.Width(50));
            float triggerOpacity = Slide(_plugin.ShowTriggersOpacity.Value, 0.02f, 1f, "Solid trigger box opacity.", GUILayout.Width(160));
            GUILayout.Label(Mathf.RoundToInt(triggerOpacity * 100f) + "%", GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (Math.Abs(triggerOpacity - _plugin.ShowTriggersOpacity.Value) > 0.005f)
                _plugin.ShowTriggersOpacity.Value = triggerOpacity;
            _plugin.HudShowInputDisplay.Value = Tog(_plugin.HudShowInputDisplay.Value,
                "Input display (every action lights up while held)",
                "Rows of key boxes in the HUD that light up when you or the script press something.");
            _plugin.HudShowPointer.Value = Tog(_plugin.HudShowPointer.Value,
                "Pointer row (mouse position in pixels and fractions, for menu scripts)",
                "Shows where the mouse is, ready to copy into mouse / mousef commands.");
            EndCard();

            BeginCard("HUD layout");
            GUILayout.BeginHorizontal();
            Lbl("Corner", "Which screen corner the info panel is attached to.", GUILayout.Width(150));
            int cornerNow = IndexOf(CornerValues, _plugin.HudCorner.Value);
            int cornerPicked = ChoiceRow(cornerNow, CornerLabels, CornerTips, 0f);
            if (cornerPicked != cornerNow)
                _plugin.HudCorner.Value = CornerValues[cornerPicked];
            GUILayout.EndHorizontal();
            OffsetRow("Offset (px)", _plugin.HudOffsetX, _plugin.HudOffsetY, "Distance from the chosen corner, in pixels.");
            SliderRow("Scale", _plugin.HudScale, 0.5f, 3f, "Size of the whole info panel.");
            SliderRow("Opacity", _plugin.HudOpacity, 0.1f, 1f, "How see-through the info panel is.");
            ColorRow("Text color", _plugin.HudColor, "Colour of the panel text, as #RRGGBB.");
            _plugin.HudBackground.Value = Tog(_plugin.HudBackground.Value, "Dark background behind the text",
                "Draw a dark box behind the panel so the text stays readable.");
            _plugin.HudGameFont.Value = Tog(_plugin.HudGameFont.Value, "Use ULTRAKILL's font (VCR OSD Mono)",
                "Use the game's own pixel font in the panel when it can be found.");
            EndCard();

            BeginCard("Speedometer");
            _plugin.SpeedoEnabled.Value = Tog(_plugin.SpeedoEnabled.Value,
                "Show the speedometer (centre of the screen, BunnyMod XT style)",
                "A big speed number under the crosshair.");
            GUILayout.BeginHorizontal();
            Lbl("Shows", "Which speed the number measures.", GUILayout.Width(150));
            int modeNow = IndexOf(SpeedoModeValues, _plugin.SpeedoMode.Value);
            int modePicked = ChoiceRow(modeNow, SpeedoModeLabels, SpeedoModeTips, 0f);
            if (modePicked != modeNow)
                _plugin.SpeedoMode.Value = SpeedoModeValues[modePicked];
            GUILayout.EndHorizontal();
            IntSliderRow("Decimals", _plugin.SpeedoDecimals, 0, 3, "Digits after the decimal point.");
            IntSliderRow("Size (px)", _plugin.SpeedoSize, 12, 160, "Height of the number in pixels.");
            OffsetRow("Offset from centre", _plugin.SpeedoOffsetX, _plugin.SpeedoOffsetY, "Move the number away from the screen centre. Positive Y is down.");
            SliderRow("Opacity", _plugin.SpeedoOpacity, 0.1f, 1f, "How see-through the number is.");
            ColorRow("Base color", _plugin.SpeedoColor, "Colour of the number, as #RRGGBB.");
            _plugin.SpeedoColorByChange.Value = Tog(_plugin.SpeedoColorByChange.Value,
                "Green while speeding up, red while slowing down",
                "Tint the number green when you gain speed and red when you lose it.");
            _plugin.SpeedoShadow.Value = Tog(_plugin.SpeedoShadow.Value, "Outline / shadow",
                "Dark outline so the number stays readable on bright backgrounds.");
            _plugin.SpeedoUnit.Value = Tog(_plugin.SpeedoUnit.Value, "Show unit (u/s)", "Append the unit after the number.");
            _plugin.SpeedoShowVertical.Value = Tog(_plugin.SpeedoShowVertical.Value, "Second line: vertical speed", "A smaller line with the up / down speed.");
            _plugin.SpeedoShowPeak.Value = Tog(_plugin.SpeedoShowPeak.Value, "Second line: peak speed this run", "A smaller line with the highest speed of this run.");
            GUILayout.BeginHorizontal();
            if (Btn("Center it", "Put the speedometer just under the crosshair.", GUILayout.Width(110)))
            {
                _plugin.SpeedoOffsetX.Value = 0;
                _plugin.SpeedoOffsetY.Value = 60;
            }
            if (Btn("Reset peak", "Forget the highest speed seen so far.", GUILayout.Width(110)))
                _peakSpeed = 0f;
            if (Btn("Reset HUD layout", "Put the info panel back in the top left with default size and colour.", GUILayout.Width(140)))
            {
                _plugin.HudCorner.Value = "topleft";
                _plugin.HudOffsetX.Value = 10;
                _plugin.HudOffsetY.Value = 10;
                _plugin.HudScale.Value = 1f;
                _plugin.HudOpacity.Value = 1f;
                _plugin.HudColor.Value = "#FFFFFF";
                _plugin.HudBackground.Value = true;
            }
            GUILayout.EndHorizontal();
            EndCard();

            BeginCard("Paused label");
            _plugin.PausedLabelEnabled.Value = Tog(_plugin.PausedLabelEnabled.Value,
                "Show PAUSED above the crosshair while the game is paused",
                "A small PAUSED text in ULTRAKILL's font above the crosshair whenever the TAS clock is paused.");
            IntSliderRow("Size (px)", _plugin.PausedLabelSize, 12, 96, "Height of the PAUSED text in pixels.");
            IntSliderRow("Height above centre", _plugin.PausedLabelOffsetY, 10, 400, "How many pixels above the crosshair the label sits.");
            EndCard();

            BeginCard("Determinism");
            _plugin.ForceSystemRandomSeeds.Value = Tog(_plugin.ForceSystemRandomSeeds.Value,
                "Force deterministic System.Random seeds (requires restart to unpatch)",
                "Make the game's own random numbers repeatable between runs.");
            _plugin.CaptureStartState.Value = Tog(_plugin.CaptureStartState.Value,
                "Capture a start state on Play (enables Rewind)",
                "Snapshot the game when Play starts so Rewind can come back to it.");
            _plugin.SavestateCleanupSpawned.Value = Tog(_plugin.SavestateCleanupSpawned.Value,
                "Savestate: destroy coins, projectiles and enemies spawned after the save",
                "When a savestate loads, remove things that were created after it was saved.");
            _plugin.PauseOnSceneChange.Value = Tog(_plugin.PauseOnSceneChange.Value,
                "Pause playback when a scene loads",
                "Stop the run when a new level loads instead of carrying on.");
            EndCard();

            BeginCard("Startup");
            GUILayout.BeginHorizontal();
            Lbl("autoload", "A script loaded automatically when the game starts.", GUILayout.Width(70));
            _plugin.Autoload.Value = Field(_plugin.Autoload.Value, "Path of the script to load at boot. Empty = none.");
            GUILayout.EndHorizontal();
            _plugin.Autoplay.Value = Tog(_plugin.Autoplay.Value, "Autoplay loaded script on boot", "Start the autoload script as soon as the game boots.");
            Hint("Path may be absolute or relative to " + _plugin.ScriptDirectory);
            GUILayout.BeginHorizontal();
            Lbl("record file", "File a recording is saved to when no script file is loaded. Relative names go to the script folder.", GUILayout.Width(80));
            _plugin.RecordingFile.Value = Field(_plugin.RecordingFile.Value, "File name, for example recording.uktas.", GUILayout.Width(260));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Lbl("default seed", "Seed written into new scripts.", GUILayout.Width(85));
            _plugin.DefaultSeed.Value = IntField(_plugin.DefaultSeed.Value, 90, "Seed written into new scripts.");
            Hint("native step " + _time.NativeFixedStep.ToString("0.####") + "s (fixed)");
            GUILayout.EndHorizontal();
            _plugin.GuiVisibleOnStart.Value = Tog(_plugin.GuiVisibleOnStart.Value, "Show this window on game start",
                "Open the TAS window as soon as the game starts.");
            EndCard();

            GUILayout.BeginHorizontal();
            if (Btn("Save config", "Write every plugin setting to the BepInEx config file now.", GUILayout.Width(120)))
                _plugin.ConfigFile.Save();
            GUILayout.EndHorizontal();
        }
    }
}
