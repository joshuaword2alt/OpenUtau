﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using K4os.Hash.xxHash;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Render {
    public class RenderPhone {
        public readonly int position;
        public readonly int duration;
        public readonly int leading;
        public readonly string phoneme;
        public readonly int tone;

        // classic args
        public readonly string resampler;
        public readonly string flags;
        public readonly float volume;
        public readonly float velocity;
        public readonly float modulation;
        public readonly float preutterMs;
        public readonly Vector2[] envelope;

        public readonly UOto oto;
        public readonly uint hash;

        internal RenderPhone(UProject project, UTrack track, UVoicePart part, UNote note, UPhoneme phoneme) {
            position = note.position + phoneme.position;
            duration = phoneme.Duration;
            leading = (int)Math.Round(project.MillisecondToTick(phoneme.preutter) / 5.0) * 5; // TODO
            this.phoneme = phoneme.phoneme;
            tone = note.tone;

            int eng = (int)phoneme.GetExpression(project, track, "eng").Item1;
            if (project.expressions.TryGetValue("eng", out var descriptor)) {
                if (eng < 0 || eng >= descriptor.options.Length) {
                    eng = 0;
                }
                resampler = descriptor.options[eng];
                if (string.IsNullOrEmpty(resampler)) {
                    resampler = Util.Preferences.Default.Resampler;
                }
                if (ResamplerDriver.ResamplerDrivers.GetResampler(resampler) == null) {
                    resampler = ResamplerDriver.ResamplerDrivers.GetDefaultResamplerName();
                }
            }
            flags = phoneme.GetResamplerFlags(project, track);
            volume = phoneme.GetExpression(project, track, "vol").Item1 * 0.01f;
            velocity = phoneme.GetExpression(project, track, "vel").Item1 * 0.01f;
            modulation = phoneme.GetExpression(project, track, "mod").Item1 * 0.01f;
            preutterMs = phoneme.preutter;
            envelope = phoneme.envelope.data.ToArray();

            oto = phoneme.oto;
            hash = Hash();
        }

        public uint Hash() {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(duration);
                    writer.Write(phoneme ?? "");
                    writer.Write(tone);

                    writer.Write(resampler ?? "");
                    writer.Write(flags ?? "");
                    writer.Write(volume);
                    writer.Write(velocity);
                    writer.Write(modulation);
                    writer.Write(preutterMs);
                    return XXH32.DigestOf(stream.ToArray());
                }
            }
        }
    }

    public class RenderPhrase {
        public readonly string singerId;
        public readonly int position;
        public readonly double tempo;
        public readonly double tickToMs;
        public readonly RenderPhone[] phones;
        public readonly float[] pitches;

        internal RenderPhrase(UProject project, UTrack track, UVoicePart part, IEnumerable<UPhoneme> phonemes) {
            var notes = new List<UNote>();
            notes.Add(phonemes.First().Parent);
            while (notes.Last() != phonemes.Last().Parent) {
                notes.Add(notes.Last().Next);
            }
            var tail = notes.Last();
            var next = tail.Next;
            while (next != null && next.Extends == tail) {
                notes.Add(next);
                next = next.Next;
            }
            phones = phonemes
                .Select(p => new RenderPhone(project, track, part, p.Parent, p))
                .ToArray();

            singerId = track.Singer.Id;
            position = part.position;
            tempo = project.bpm;
            tickToMs = 60000.0 / project.bpm * project.beatUnit / 4 / project.resolution;

            const int pitchInterval = 5;
            int pitchStart = phones[0].position - phones[0].leading;
            pitches = new float[(phones.Last().position + phones.Last().duration - pitchStart) / pitchInterval + 1];
            int index = 0;
            foreach (var note in notes) {
                while (pitchStart + index * pitchInterval < note.End && index < pitches.Length) {
                    pitches[index] = note.tone * 100;
                    index++;
                }
            }
            while (index < pitches.Length) {
                pitches[index] = pitches[index - 1];
                index++;
            }
            foreach (var note in notes) {
                if (note.vibrato.length <= 0) {
                    continue;
                }
                int startIndex = Math.Max(0, (note.position - pitchStart) / pitchInterval);
                int endIndex = Math.Min(pitches.Length, (note.End - pitchStart) / pitchInterval);
                for (int i = startIndex; i < endIndex; ++i) {
                    float nPos = (float)(pitchStart + i * pitchInterval - note.position) / note.duration;
                    float nPeriod = (float)project.MillisecondToTick(note.vibrato.period) / note.duration;
                    var point = note.vibrato.Evaluate(nPos, nPeriod, note);
                    pitches[i] = point.Y * 100;
                }
            }
            foreach (var note in notes) {
                var pitchPoints = note.pitch.data
                    .Select(point => new PitchPoint(
                        project.MillisecondToTick(point.X) + note.position,
                        point.Y * 10 + note.tone * 100))
                    .ToList();
                if (pitchPoints.Count == 0) {
                    pitchPoints.Add(new PitchPoint(note.position, note.tone * 100));
                    pitchPoints.Add(new PitchPoint(note.End, note.tone * 100));
                }
                if (note == notes.First() && pitchPoints[0].X > pitchStart) {
                    pitchPoints.Insert(0, new PitchPoint(pitchStart, pitchPoints[0].Y));
                } else if (pitchPoints[0].X > note.position) {
                    pitchPoints.Insert(0, new PitchPoint(note.position, pitchPoints[0].Y));
                }
                if (pitchPoints.Last().X < note.End) {
                    pitchPoints.Add(new PitchPoint(note.End, pitchPoints.Last().Y));
                }
                PitchPoint lastPoint = pitchPoints[0];
                index = Math.Max(0, (int)Math.Ceiling((lastPoint.X - pitchStart) / pitchInterval));
                foreach (var point in pitchPoints.Skip(1)) {
                    int x;
                    while ((x = pitchStart + index * pitchInterval) < point.X && index < pitches.Length) {
                        float pitch = (float)MusicMath.InterpolateShape(lastPoint.X, point.X, lastPoint.Y, point.Y, x, lastPoint.shape);
                        float basePitch = x >= note.position
                            ? note.tone * 100
                            : (note == notes.First() ? note : note.Prev).tone * 100;
                        pitches[index] += pitch - basePitch;
                        index++;
                    }
                    lastPoint = point;
                }
            }
        }

        public static List<RenderPhrase> FromPart(UProject project, UTrack track, UVoicePart part) {
            var phrases = new List<RenderPhrase>();
            var phonemes = part.notes
                .Where(note => !note.OverlapError)
                .SelectMany(note => note.phonemes.Where(phoneme => !phoneme.Error))
                .ToList();
            if (phonemes.Count == 0) {
                return phrases;
            }
            var phrasePhonemes = new List<UPhoneme>() { phonemes[0] };
            for (int i = 1; i < phonemes.Count; ++i) {
                if (phonemes[i - 1].Parent.position + phonemes[i - 1].End != phonemes[i].Parent.position + phonemes[i].position) {
                    phrases.Add(new RenderPhrase(project, track, part, phrasePhonemes));
                    phrasePhonemes.Clear();
                }
                phrasePhonemes.Add(phonemes[i]);
            }
            if (phrasePhonemes.Count > 0) {
                phrases.Add(new RenderPhrase(project, track, part, phrasePhonemes));
                phrasePhonemes.Clear();
            }
            return phrases;
        }
    }
}
