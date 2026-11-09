using Game.Citizens;
using Game.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Entities;

namespace RealisticEMS
{
    public enum AmpdsLevel { Omega, Alpha, Bravo, Charlie, Delta, Echo }
    public enum CallMix { Realistic, Balanced, Dramatic }
    public enum CallCause { None, TrafficAccident, Fire, Weather, Flood, Destruction, OtherEvent }

    /// <summary>One row of Data/einsatzcodes.csv (Einsatzcodes Wien, einsatzcodes.at, Stand 06.01.2025).</summary>
    public class AmpdsCode
    {
        public int Protocol;
        public AmpdsLevel Level;
        public int Determinant;
        public string ComplaintDe, ComplaintEn, ReasonDe, ReasonEn;
        public string Complaint => Mod.T(ComplaintDe, ComplaintEn);
        public string Reason => Mod.T(ReasonDe, ReasonEn);
        public string RawUnits;
        // Response: only RTW, NEF and AED count. KTW/NKT/1450 become one non-urgent RTW (the game always transports).
        public int Rtw, Nef, Aed;
        // Police cars sent by ResponderDispatchSystem. Psychiatry (25): 1 for O/A up to 4 for D/E. AED (First Responder): 2.
        public int Police => Protocol == 25 ? Math.Max(1, Math.Min(4, (int)Level)) : Aed > 0 ? 2 : 0;
        public int Fire => Aed > 0 ? 1 : 0; // fire brigade as First Responder with the AED
        public bool Urgent => Level >= AmpdsLevel.Charlie;

        public override string ToString() => $"{Protocol:00}-{Level.ToString()[0]}-{Determinant:00}";

        public string UnitsText()
        {
            var parts = new List<string>();
            string rtw = Mod.T("RTW", "AMB"), police = Mod.T("Polizei", "Police");
            if (Rtw > 0) parts.Add(Rtw > 1 ? $"{Rtw} {rtw}" : rtw);
            if (Nef > 0) parts.Add(Mod.T("NEF", "DOC"));
            if (Aed > 0) parts.Add("AED");
            if (Police > 0) parts.Add(Police > 1 ? $"{Police} {police}" : police);
            if (Fire > 0) parts.Add(Mod.T("Feuerwehr", "Fire"));
            return string.Join(" + ", parts);
        }
    }

    public static class Ampds
    {
        public static List<AmpdsCode> Codes = new List<AmpdsCode>();

        // Which complaint groups (MPDS protocols) fit which game situation.
        private static readonly int[] SickPool = { 1, 2, 5, 6, 10, 11, 12, 13, 18, 19, 20, 23, 25, 26, 28, 31, 32 };
        private static readonly int[] SickChildPool = { 1, 2, 6, 11, 12, 13, 20, 23, 26, 31, 32 };
        private static readonly int[] InjuryPool = { 3, 4, 16, 17, 21, 27, 30 };
        private static readonly int[] ElderlyInjuryPool = { 17, 17, 17, 21, 30 }; // repeated = weighted towards falls
        private static readonly int[] PregnancyPool = { 24 };

        public static void Load()
        {
            using var stream = typeof(Ampds).Assembly.GetManifestResourceStream("RealisticEMS.Data.einsatzcodes.csv");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Codes = Parse(reader.ReadToEnd());
            Mod.log.Info($"Loaded {Codes.Count} Einsatzcodes");
        }

        public static List<AmpdsCode> Parse(string csv)
        {
            var list = new List<AmpdsCode>();
            foreach (var line in csv.Split('\n').Skip(1))
            {
                var p = line.TrimEnd('\r').Split(';');
                if (p.Length < 4 || p[0].Length != 5) continue;
                int lvl = "OABCDE".IndexOf(p[0][2]);
                if (lvl < 0) continue;
                var c = new AmpdsCode
                {
                    Protocol = int.Parse(p[0].Substring(0, 2)), Level = (AmpdsLevel)lvl, Determinant = int.Parse(p[0].Substring(3, 2)),
                    ComplaintDe = p[1], ReasonDe = p[2], RawUnits = p[3],
                    ComplaintEn = p.Length > 5 ? p[4] : p[1], ReasonEn = p.Length > 5 ? p[5] : p[2],
                };
                ParseUnits(c);
                list.Add(c);
            }
            return list;
        }

        /// <summary>"2 RTW / NEF / FISU / AED" -> Rtw 2, Nef 1, Aed 1. Everything except RTW/NEF/AED is ignored.</summary>
        public static void ParseUnits(AmpdsCode c)
        {
            foreach (Match m in Regex.Matches(c.RawUnits, @"(?:(\d+)\s*)?\b(RTW|NEF|AED|KTW|NKT)\b"))
            {
                int n = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1;
                switch (m.Groups[2].Value)
                {
                    case "RTW": c.Rtw = Math.Max(c.Rtw, n); break;
                    case "NEF": c.Nef = Math.Max(c.Nef, n); break;
                    case "AED": c.Aed = Math.Max(c.Aed, n); break;
                }
            }
            if (c.Rtw == 0) c.Rtw = 1; // KTW, NKT, 1450, NEF-only: the game patient still needs transport
        }

        // Level weights O,A,B,C,D,E per health band (<15, <40, <60, rest), one table per CallMix preset.
        // The level is picked first, so the CSV's many Delta rows no longer skew the result.
        // ponytail: hand-tuned heuristic; edit the numbers here if a preset feels off.
        private static readonly int[][][] Mix =
        {
            new[] { new[] { 0, 0, 1, 4, 3, 1 }, new[] { 0, 2, 4, 3, 1, 0 }, new[] { 1, 4, 3, 1, 0, 0 }, new[] { 3, 4, 2, 0, 0, 0 } }, // Realistic
            new[] { new[] { 0, 0, 0, 1, 2, 1 }, new[] { 0, 0, 1, 2, 1, 0 }, new[] { 0, 1, 2, 2, 1, 0 }, new[] { 1, 2, 2, 0, 0, 0 } }, // Balanced
            new[] { new[] { 0, 0, 0, 0, 3, 1 }, new[] { 0, 0, 0, 1, 2, 0 }, new[] { 0, 0, 1, 1, 2, 0 }, new[] { 1, 1, 1, 0, 0, 0 } }, // Dramatic (about the old mix)
        };
        private static readonly int[] DeadMix = { 0, 0, 0, 0, 1, 1 };

        public static int[] WeightsFor(int health, bool dead, CallMix mix) =>
            dead ? DeadMix : Mix[(int)mix][health < 15 ? 0 : health < 40 ? 1 : health < 60 ? 2 : 3];

        public static int[] PoolFor(HealthProblemFlags flags, CallCause cause, CitizenAge age, bool female, int seed)
        {
            if ((flags & HealthProblemFlags.Dead) != 0) return new[] { 9 };
            switch (cause)
            {
                case CallCause.TrafficAccident: return new[] { 29 };
                case CallCause.Fire: return new[] { 7, 7, 8 };
                case CallCause.Weather: return new[] { 15, 20 };
                case CallCause.Flood: return new[] { 14 };
                case CallCause.Destruction: return new[] { 22 };
            }
            if ((flags & HealthProblemFlags.Injured) != 0) return age == CitizenAge.Elderly ? ElderlyInjuryPool : InjuryPool;
            if (age == CitizenAge.Child || age == CitizenAge.Teen) return SickChildPool;
            if (female && age == CitizenAge.Adult && seed % 10 == 0) return PregnancyPool;
            return SickPool;
        }

        /// <summary>Picks a code: protocol from the situation pool, level from health, then one code of that set. Stable per seed.</summary>
        public static AmpdsCode Pick(HealthProblemFlags flags, CallCause cause, CitizenAge age, bool female, int health, int seed, CallMix mix = CallMix.Realistic)
        {
            var rnd = new Random(seed);
            var pool = PoolFor(flags, cause, age, female, seed);
            int protocol = pool[rnd.Next(pool.Length)];
            // Determinant 00 is the "Aufwertung" (override) row, not a real call.
            var all = Codes.Where(c => c.Protocol == protocol && c.Determinant != 0).ToList();
            if (all.Count == 0) return null;
            var w = WeightsFor(health, (flags & HealthProblemFlags.Dead) != 0, mix);
            // Only levels this protocol has; weights of missing levels drop out.
            var present = Enumerable.Range(0, 6).Where(l => w[l] > 0 && all.Any(c => (int)c.Level == l)).ToList();
            int level;
            if (present.Count > 0)
            {
                int r = rnd.Next(present.Sum(l => w[l]));
                level = present.First(l => (r -= w[l]) < 0);
            }
            else // e.g. 29 has no C: take the nearest level of that protocol to the heaviest wanted one
            {
                int want = Array.IndexOf(w, w.Max());
                level = (int)all.OrderBy(c => Math.Abs((int)c.Level - want)).First().Level;
            }
            var candidates = all.Where(c => (int)c.Level == level).ToList();
            return candidates[rnd.Next(candidates.Count)];
        }

        /// <summary>Debug tab: code a patient gets on its next call instead of the situation pick. Consumed by Classify.</summary>
        public static readonly Dictionary<Entity, AmpdsCode> Forced = new Dictionary<Entity, AmpdsCode>();

        public static AmpdsCode Classify(EntityManager em, Entity patient)
        {
            if (Forced.TryGetValue(patient, out var forced)) { Forced.Remove(patient); return forced; }
            var hp = em.HasComponent<HealthProblem>(patient) ? em.GetComponentData<HealthProblem>(patient) : new HealthProblem(Entity.Null, HealthProblemFlags.Sick);
            var cause = CallCause.None;
            var ev = hp.m_Event;
            if (ev != Entity.Null && em.Exists(ev))
                cause = em.HasComponent<TrafficAccident>(ev) ? CallCause.TrafficAccident
                      : em.HasComponent<Fire>(ev) ? CallCause.Fire
                      : em.HasComponent<Flood>(ev) ? CallCause.Flood
                      : em.HasComponent<WeatherPhenomenon>(ev) ? CallCause.Weather
                      : em.HasComponent<Destruction>(ev) ? CallCause.Destruction
                      : CallCause.OtherEvent;
            var c = em.HasComponent<Citizen>(patient) ? em.GetComponentData<Citizen>(patient) : default;
            bool female = (c.m_State & CitizenFlags.Male) == 0;
            return Pick(hp.m_Flags, cause, c.GetAge(), female, c.m_Health, patient.Index * 31 + patient.Version, Mod.Setting?.CallMix ?? CallMix.Realistic);
        }

        /// <summary>Runs at load; logs an error if parsing or selection regresses.</summary>
        public static void SelfCheck()
        {
            void Expect(bool ok, string what) { if (!ok) Mod.log.Error("Ampds self-check failed: " + what); }
            var c = new AmpdsCode { RawUnits = "2 RTW / NEF / FISU / AED / OA" };
            ParseUnits(c);
            Expect(c.Rtw == 2 && c.Nef == 1 && c.Aed == 1 && c.Police == 2 && c.Fire == 1, "unit parse 2 RTW/NEF/AED");
            c = new AmpdsCode { RawUnits = "ad 1450 (TEWEB) oder KTW" };
            ParseUnits(c);
            Expect(c.Rtw == 1 && c.Nef == 0 && c.Aed == 0, "KTW -> 1 RTW");
            Expect(new AmpdsCode { Protocol = 25, Level = AmpdsLevel.Alpha }.Police == 1 && new AmpdsCode { Protocol = 25, Level = AmpdsLevel.Charlie }.Police == 3
                && new AmpdsCode { Protocol = 25, Level = AmpdsLevel.Echo }.Police == 4, "psychiatry police 1-4");
            Expect(Codes.Count > 500, $"code count {Codes.Count}");
            var arrest = Pick(HealthProblemFlags.Dead, CallCause.None, CitizenAge.Adult, false, 0, 1);
            Expect(arrest != null && arrest.Protocol == 9 && arrest.Level >= AmpdsLevel.Delta, "dead -> 09 D/E");
            var crash = Pick(HealthProblemFlags.Injured, CallCause.TrafficAccident, CitizenAge.Adult, false, 80, 7);
            Expect(crash != null && crash.Protocol == 29, "traffic -> 29");
            int Deltas(CallMix m) => Enumerable.Range(0, 1000).Count(s => Pick(HealthProblemFlags.Sick, CallCause.None, CitizenAge.Adult, false, 30, s, m)?.Level >= AmpdsLevel.Delta);
            Expect(Deltas(CallMix.Realistic) < Deltas(CallMix.Balanced) && Deltas(CallMix.Balanced) < Deltas(CallMix.Dramatic), "call mix presets order D/E share");
        }
    }
}
