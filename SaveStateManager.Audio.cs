using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Audio module.
    ///
    /// The core capture keeps the AudioSources that hang off the player and enemies. Level
    /// ambience, boss music and one-shot world sounds live elsewhere: their playback position,
    /// pitch, volume, mixer routing and filter state, plus MusicManager's own playback
    /// bookkeeping, decide what is heard after a load. Full saves keep every world source;
    /// rolling history still limits the walk to playing sources.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        const int MaxWorldAudio = 1024;
        static readonly string[] MusicTypeNames = { "MusicManager", "MusicController", "Music" };

        sealed class AudioModuleState
        {
            public ComponentSnapshot Music;
            public string MusicType;
            public List<AudioSnapshot> Sources = new List<AudioSnapshot>();
        }

        partial void CaptureAudio(Savestate state)
        {
            AudioModuleState audio = new AudioModuleState();
            for (int t = 0; t < MusicTypeNames.Length && audio.Music == null; t++)
            {
                Type type = _bridge.FindType(MusicTypeNames[t]);
                if (type == null)
                    continue;
                try
                {
                    UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(type);
                    Component component = found as Component;
                    if (component == null)
                        continue;
                    audio.Music = CaptureComponent(component, RefCap);
                    audio.MusicType = component.GetType().Name;
                }
                catch
                {
                }
            }

            // A per-tick rolling snapshot skips the scene-wide emitter walk; the player's own
            // sources are already part of the rig capture.
            UnityEngine.Object[] sources;
            if (_lightCapture)
                sources = new UnityEngine.Object[0];
            else
                try { sources = UnityEngine.Object.FindObjectsOfType(typeof(AudioSource)); }
                catch { sources = new UnityEngine.Object[0]; }
            for (int i = 0; i < sources.Length && audio.Sources.Count < MaxWorldAudio; i++)
            {
                AudioSource source = sources[i] as AudioSource;
                if (source == null || source.clip == null || (_lightCapture && !source.isPlaying))
                    continue;
                if (IsTemplateClone(source.gameObject))
                    continue;
                AudioSnapshot snapshot = CaptureAudioSource(source);
                if (snapshot != null)
                    audio.Sources.Add(snapshot);
            }
            if (audio.Sources.Count >= MaxWorldAudio)
                NoteCap("playing world audio sources", MaxWorldAudio);

            SetExt(state, "Audio", audio);
        }

        partial void RestoreAudio(Savestate state, ref int restored, ref int missing)
        {
            AudioModuleState audio = GetExt<AudioModuleState>(state, "Audio");
            if (audio == null)
                return;

            if (audio.Music != null)
            {
                Component music = Resolve(audio.Music.Component);
                if (music == null && !string.IsNullOrEmpty(audio.MusicType))
                    music = FindMusicComponent(audio.MusicType);
                if (music != null)
                {
                    try
                    {
                        RestoreComponent(audio.Music);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        _plugin.LogWarning("Music restore skipped: " + ex.Message);
                    }
                }
            }

            // Positions and pitch of world sources; RestoreAudio ignores destroyed sources.
            int before = 0;
            for (int i = 0; i < audio.Sources.Count; i++)
            {
                AudioSource source = audio.Sources[i].Source;
                if (source != null && GameBridge.IsAlive(source))
                    before++;
            }
            RestoreAudio(audio.Sources);
            restored += before;
            missing += audio.Sources.Count - before;
        }

        Component FindMusicComponent(string typeName)
        {
            Type type = _bridge.FindType(typeName);
            if (type == null)
                return null;
            try
            {
                return UnityEngine.Object.FindObjectOfType(type) as Component;
            }
            catch
            {
                return null;
            }
        }
    }
}
