using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// ULTRAKILL's own HUD font (VCR OSD Mono), drawn straight out of the game's TextMeshPro
    /// glyph atlas. The game ships the font as a baked atlas rather than as a Font asset IMGUI
    /// can use, so the speedometer draws the atlas quads itself: the same glyphs and spacing the
    /// game's HUD shows, at any size.
    /// </summary>
    public sealed partial class GuiManager
    {
        struct AtlasGlyph
        {
            public bool usable;
            public Texture texture;
            public Rect texCoords;
            public float width;     // quad size in font points
            public float height;
            public float bearingX;  // pen position to the left edge of the quad
            public float bearingY;  // baseline up to the top edge of the quad
            public float advance;
            public float scale;     // per-glyph multiplier, 1 for the game's fonts
        }

        UnityEngine.Object _fontAtlas;
        string _fontAtlasName = string.Empty;
        Texture[] _fontAtlasTextures;
        Texture[] _fontAtlasMasks;
        IDictionary _fontAtlasLookup;
        IEnumerable _fontAtlasCharacterTable;
        float _fontAtlasPointSize = 90f;
        float _fontAtlasScale = 1f;
        float _nextFontAtlasTry;
        int _fontAtlasTries;
        readonly Dictionary<char, AtlasGlyph> _atlasGlyphs = new Dictionary<char, AtlasGlyph>();

        /// <summary>
        /// Locates the game's font asset once and keeps it. The scan walks every loaded object,
        /// so it is paced like the Font search: rarely, and never right after a scene change.
        /// </summary>
        bool EnsureFontAtlas()
        {
            if (_fontAtlas != null)
                return true;
            if (TimeController.RealTime < _nextFontAtlasTry)
                return false;
            _fontAtlasTries++;
            _nextFontAtlasTry = Math.Max(TimeController.RealTime + (_fontAtlasTries < 3 ? 5f : 60f), _noFontSearchUntil);
            _fontAtlas = FindFontAtlas();
            if (_fontAtlas != null)
                ReadFontAtlas();
            return _fontAtlas != null;
        }

        /// <summary>
        /// Picks the font asset the game's own text uses. The HUD font is named VCR OSD Mono, so a
        /// name match wins; otherwise the asset used by the most TMP text objects is taken.
        /// </summary>
        UnityEngine.Object FindFontAtlas()
        {
            try
            {
                Type assetType = _bridge.FindType("TMP_FontAsset");
                if (assetType == null)
                    return null;
                UnityEngine.Object[] assets = Resources.FindObjectsOfTypeAll(assetType);
                if (assets == null || assets.Length == 0)
                    return null;

                // How many of the game's text objects render with each asset.
                Dictionary<UnityEngine.Object, int> used = new Dictionary<UnityEngine.Object, int>();
                Type textType = _bridge.FindType("TMP_Text");
                if (textType != null)
                {
                    UnityEngine.Object[] texts = Resources.FindObjectsOfTypeAll(textType);
                    for (int i = 0; i < texts.Length; i++)
                    {
                        if (texts[i] == null)
                            continue;
                        UnityEngine.Object font = _bridge.GetMember(texts[i], new[] { "font", "m_fontAsset" }) as UnityEngine.Object;
                        if (font == null)
                            continue;
                        int count;
                        used.TryGetValue(font, out count);
                        used[font] = count + 1;
                    }
                }

                UnityEngine.Object best = null;
                string bestName = string.Empty;
                int bestScore = 0;
                for (int i = 0; i < assets.Length; i++)
                {
                    UnityEngine.Object asset = assets[i];
                    if (asset == null)
                        continue;
                    string name = (asset.name ?? string.Empty).ToLowerInvariant();
                    int score = 0;
                    if (name.Contains("vcr"))
                        score += 6;
                    else if (name.Contains("osd") || name.Contains("mono"))
                        score += 4;
                    int users;
                    if (used.TryGetValue(asset, out users))
                        score += Math.Min(users, 3);
                    if (score == 0)
                        continue;
                    if (score > bestScore || (score == bestScore && name.Length < bestName.Length))
                    {
                        best = asset;
                        bestName = name;
                        bestScore = score;
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Speedometer font: font asset search failed: " + ex.Message);
            }
            return null;
        }

        void ReadFontAtlas()
        {
            _atlasGlyphs.Clear();
            _fontAtlasName = _fontAtlas.name;
            Texture2D[] textures = _bridge.GetMember(_fontAtlas, new[] { "atlasTextures", "m_AtlasTextures" }) as Texture2D[];
            _fontAtlasLookup = _bridge.GetMember(_fontAtlas, new[] { "characterLookupTable", "m_CharacterLookupTable" }) as IDictionary;
            _fontAtlasCharacterTable = _fontAtlasLookup == null
                ? _bridge.GetMember(_fontAtlas, new[] { "characterTable", "m_CharacterTable" }) as IEnumerable
                : null;

            object face = _bridge.GetMember(_fontAtlas, new[] { "faceInfo", "m_FaceInfo" });
            float pointSize = ReadAtlasNumber(face, "pointSize", 90f);
            float scale = ReadAtlasNumber(face, "scale", 1f);
            _fontAtlasPointSize = pointSize > 0f ? pointSize : 90f;
            _fontAtlasScale = scale > 0f ? scale : 1f;

            if (textures == null || textures.Length == 0 || (_fontAtlasLookup == null && _fontAtlasCharacterTable == null))
            {
                _plugin.LogWarning("Speedometer font: '" + _fontAtlasName + "' has no readable glyph atlas; keeping the system font.");
                _fontAtlas = null;
                _fontAtlasTextures = null;
                return;
            }

            // The game's atlas is a glyph mask whose colour channels are black (an alpha-only or
            // distance-field texture), so drawing it directly paints black silhouettes. Each page
            // is copied once into a white, transparent-background mask IMGUI can tint.
            _fontAtlasTextures = textures;
            _fontAtlasMasks = new Texture[textures.Length];
            if (MaskTexture(0) == null)
            {
                _plugin.LogWarning("Speedometer font: the glyph atlas of '" + _fontAtlasName + "' cannot be drawn; keeping the system font.");
                _fontAtlas = null;
                _fontAtlasTextures = null;
                _fontAtlasMasks = null;
                return;
            }

            _plugin.LogInfo("Speedometer font: drawing ULTRAKILL's '" + _fontAtlasName + "' glyph atlas (" +
                            textures[0].width + "x" + textures[0].height + ", " + textures[0].format + ").");
        }

        Texture MaskTexture(int index)
        {
            if (_fontAtlasMasks == null || index < 0 || index >= _fontAtlasMasks.Length)
                return null;
            Texture mask = _fontAtlasMasks[index];
            if (mask != null)
                return mask;
            Texture2D source = index < _fontAtlasTextures.Length ? _fontAtlasTextures[index] as Texture2D : null;
            if (source == null)
                return null;
            Color32[] pixels = ReadAtlasPixels(source);
            if (pixels == null)
            {
                _plugin.LogWarning("Speedometer font: could not read the glyph atlas '" + source.name + "'; keeping the system font.");
                return null;
            }
            mask = BuildMask(pixels, source.width, source.height);
            _fontAtlasMasks[index] = mask;
            return mask;
        }

        /// <summary>
        /// Copies the atlas pixels: directly when the texture is readable, otherwise through a
        /// render texture, since the atlas is normally an asset that cannot be read back.
        /// </summary>
        Color32[] ReadAtlasPixels(Texture2D source)
        {
            try
            {
                return source.GetPixels32();
            }
            catch
            {
            }
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = null;
            try
            {
                temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                copy.Apply(false, false);
                Color32[] pixels = copy.GetPixels32();
                UnityEngine.Object.Destroy(copy);
                return pixels;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Speedometer font: glyph atlas copy failed: " + ex.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null)
                    RenderTexture.ReleaseTemporary(temporary);
            }
        }

        /// <summary>
        /// White glyphs on a transparent background, taken from whichever channel carries the
        /// coverage: alpha when the atlas has one, otherwise the colour (distance-field atlases
        /// store the glyph in a single grey channel).
        /// </summary>
        static Texture2D BuildMask(Color32[] pixels, int width, int height)
        {
            bool hasAlpha = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a < 250)
                {
                    hasAlpha = true;
                    break;
                }
            }
            Color32[] mask = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                int value = hasAlpha ? pixel.a : Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
                // A narrow band around the middle keeps the pixel font's edges crisp instead of
                // smearing the distance field's whole gradient into transparency.
                int alpha = (int)((value / 255f - 0.42f) / 0.16f * 255f);
                if (alpha < 0)
                    alpha = 0;
                else if (alpha > 255)
                    alpha = 255;
                mask[i] = new Color32(255, 255, 255, (byte)alpha);
            }
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = "ULTRAKILL font mask";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels32(mask);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>Reads a numeric member of TMP's structures; the bridge does the reflection.</summary>
        float ReadAtlasNumber(object target, string name, float fallback)
        {
            if (target == null)
                return fallback;
            object value = _bridge.GetMember(target, new[] { name });
            if (value is float)
                return (float)value;
            if (value is int)
                return (int)value;
            if (value is uint)
                return (uint)value;
            if (value is double)
                return (float)(double)value;
            if (value is IConvertible)
            {
                try
                {
                    return Convert.ToSingle(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }
            return fallback;
        }

        object LookupAtlasCharacter(char c)
        {
            if (_fontAtlasLookup != null)
            {
                try
                {
                    return _fontAtlasLookup[(object)(uint)c];
                }
                catch
                {
                    return null;
                }
            }
            if (_fontAtlasCharacterTable == null)
                return null;
            foreach (object character in _fontAtlasCharacterTable)
            {
                if (character == null)
                    continue;
                object unicode = _bridge.GetMember(character, new[] { "unicode", "m_Unicode" });
                if (unicode == null)
                    continue;
                try
                {
                    if (Convert.ToUInt32(unicode, CultureInfo.InvariantCulture) == (uint)c)
                        return character;
                }
                catch
                {
                }
            }
            return null;
        }

        AtlasGlyph ReadAtlasGlyph(char c)
        {
            AtlasGlyph glyph = new AtlasGlyph();
            glyph.scale = 1f;
            object character = LookupAtlasCharacter(c);
            if (character == null)
                return glyph;
            object source = _bridge.GetMember(character, new[] { "glyph", "m_Glyph" });
            if (source == null)
                return glyph;

            float scale = ReadAtlasNumber(source, "scale", 1f);
            glyph.scale = scale > 0f ? scale : 1f;
            object metrics = _bridge.GetMember(source, new[] { "metrics", "m_Metrics" });
            glyph.width = ReadAtlasNumber(metrics, "width", 0f);
            glyph.height = ReadAtlasNumber(metrics, "height", 0f);
            glyph.bearingX = ReadAtlasNumber(metrics, "horizontalBearingX", 0f);
            glyph.bearingY = ReadAtlasNumber(metrics, "horizontalBearingY", 0f);
            glyph.advance = ReadAtlasNumber(metrics, "horizontalAdvance", 0f);

            object rect = _bridge.GetMember(source, new[] { "glyphRect", "m_GlyphRect" });
            float x = ReadAtlasNumber(rect, "x", 0f);
            float y = ReadAtlasNumber(rect, "y", 0f);
            float w = ReadAtlasNumber(rect, "width", 0f);
            float h = ReadAtlasNumber(rect, "height", 0f);
            int index = Mathf.RoundToInt(ReadAtlasNumber(source, "atlasIndex", 0f));
            if (_fontAtlasTextures == null || index < 0 || index >= _fontAtlasTextures.Length)
                return glyph;
            Texture texture = MaskTexture(index);
            if (texture == null || w <= 0f || h <= 0f || glyph.width <= 0f || glyph.height <= 0f)
                return glyph;

            // Unity's TextCore measures glyphRect.y from the bottom of the atlas, the same way
            // IMGUI samples a texture, so the rectangle maps straight onto the texture coords.
            glyph.texture = texture;
            glyph.texCoords = new Rect(x / texture.width, y / texture.height, w / texture.width, h / texture.height);
            glyph.usable = true;
            return glyph;
        }

        AtlasGlyph AtlasGlyphFor(char c)
        {
            AtlasGlyph glyph;
            if (_atlasGlyphs.TryGetValue(c, out glyph))
                return glyph;
            glyph = ReadAtlasGlyph(c);
            _atlasGlyphs[c] = glyph;
            return glyph;
        }

        /// <summary>
        /// Draws the text centred on (centreX, centreY) with ULTRAKILL's own font at 'size' pixels
        /// per font point, with an optional dark outline. False means the game's font is not
        /// available yet, so the caller should fall back to a system font for this frame.
        /// </summary>
        bool DrawGameFontText(float centreX, float centreY, string text, float size, Color color, bool outline)
        {
            if (string.IsNullOrEmpty(text) || !EnsureFontAtlas())
                return false;

            float pixelsPerPoint = size / _fontAtlasPointSize * _fontAtlasScale;
            if (pixelsPerPoint <= 0f)
                return false;

            float advance = 0f;
            float top = float.MinValue;
            float bottom = float.MaxValue;
            bool any = false;
            for (int i = 0; i < text.Length; i++)
            {
                AtlasGlyph glyph = AtlasGlyphFor(text[i]);
                advance += glyph.advance * glyph.scale * pixelsPerPoint;
                if (!glyph.usable)
                    continue;
                any = true;
                top = Mathf.Max(top, glyph.bearingY * glyph.scale * pixelsPerPoint);
                bottom = Mathf.Min(bottom, (glyph.bearingY - glyph.height) * glyph.scale * pixelsPerPoint);
            }
            if (!any)
                return false;

            // Centre the actual glyph box on the requested point, then lay the run out from its
            // baseline: bearingY grows up from the baseline, screen y grows down.
            float baseline = centreY + (top + bottom) * 0.5f;
            float penX = centreX - advance * 0.5f;
            Color previous = GUI.color;

            if (outline)
            {
                float d = Mathf.Max(1.5f, size * 0.055f);
                GUI.color = new Color(0f, 0f, 0f, 0.9f * color.a);
                DrawAtlasRun(penX + d, baseline + d, text, pixelsPerPoint);
                DrawAtlasRun(penX - d, baseline + d, text, pixelsPerPoint);
                DrawAtlasRun(penX + d, baseline - d, text, pixelsPerPoint);
                DrawAtlasRun(penX - d, baseline - d, text, pixelsPerPoint);
            }

            GUI.color = color;
            DrawAtlasRun(penX, baseline, text, pixelsPerPoint);
            GUI.color = previous;
            return true;
        }

        void DrawAtlasRun(float penX, float baseline, string text, float pixelsPerPoint)
        {
            for (int i = 0; i < text.Length; i++)
            {
                AtlasGlyph glyph = AtlasGlyphFor(text[i]);
                if (glyph.usable)
                {
                    Rect quad = new Rect(penX + glyph.bearingX * glyph.scale * pixelsPerPoint,
                                         baseline - glyph.bearingY * glyph.scale * pixelsPerPoint,
                                         glyph.width * glyph.scale * pixelsPerPoint,
                                         glyph.height * glyph.scale * pixelsPerPoint);
                    GUI.DrawTextureWithTexCoords(quad, glyph.texture, glyph.texCoords, true);
                }
                penX += glyph.advance * glyph.scale * pixelsPerPoint;
            }
        }
    }
}
