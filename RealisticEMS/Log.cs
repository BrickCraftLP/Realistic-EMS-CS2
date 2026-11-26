using Colossal.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Runtime.CompilerServices
{
    // net48 lacks these; the C# 10 compiler only needs them to exist.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)] internal sealed class InterpolatedStringHandlerAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument) { }
        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments) { }
    }
}

namespace RealisticEMS
{
    /// <summary>Most to least severe; a message is written when its level &lt;= the level for its target.</summary>
    public enum LogLevel { Error, Warn, Info, Debug, Trace }

    /// <summary>
    /// Rust-style logger (log + env_logger): every line carries level and target, e.g.
    /// <c>[INFO] realistic_ems::helicopter:371: heli=12 dispatched base=3</c>.
    /// Target = source file in snake_case, filled in by the compiler.
    /// Filtering works like RUST_LOG: <see cref="Filter"/> = "info,helicopter=trace,driving=debug"
    /// (bare level = default, module=level overrides, longest matching module prefix wins).
    /// Interpolated messages are lazy: <c>Log.Trace($"... {Expensive()}")</c> builds nothing when Trace is off
    /// for every target, like Rust's macros.
    /// Error = something broke, Warn = recovered from a bad state, Info = lifecycle (load, prefabs),
    /// Debug = per-vehicle decisions and state changes, Trace = per-frame diagnostics.
    /// </summary>
    public static class Log
    {
        private const string Root = "realistic_ems";
        private static readonly ILog s_Log = InitLog();
        private static readonly Dictionary<string, string> s_Targets = new Dictionary<string, string>();
        private static readonly Dictionary<string, LogLevel> s_Resolved = new Dictionary<string, LogLevel>();

        private static LogLevel s_Default = LogLevel.Info;
        private static List<(string prefix, LogLevel level)> s_Rules = new List<(string, LogLevel)>();
        private static string s_Filter = "";
        /// <summary>Most verbose level any target can reach: the cheap check every call does first.</summary>
        private static LogLevel s_Max = LogLevel.Info;

        private static ILog InitLog()
        {
            var log = LogManager.GetLogger(nameof(RealisticEMS)).SetShowsErrorsInUI(false);
            log.effectivenessLevel = Level.All; // filtering happens here, not in Colossal
            return log;
        }

        /// <summary>Level for targets no filter rule matches.</summary>
        public static LogLevel MaxLevel
        {
            get => s_Default;
            set { lock (s_Resolved) { s_Default = value; Rebuild(); } }
        }

        /// <summary>RUST_LOG-style rules, e.g. "helicopter=trace,driving::normal_road=debug,warn". Bad parts are ignored with a warning.</summary>
        public static string Filter
        {
            get => s_Filter;
            set
            {
                var rules = new List<(string, LogLevel)>();
                LogLevel? def = null;
                var bad = new List<string>();
                foreach (var raw in (value ?? "").Split(','))
                {
                    string part = raw.Trim();
                    if (part.Length == 0) continue;
                    int eq = part.IndexOf('=');
                    string module = eq < 0 ? null : part.Substring(0, eq).Trim().ToLowerInvariant();
                    string lvl = eq < 0 ? part : part.Substring(eq + 1).Trim();
                    if (!TryParseLevel(lvl, out var level)) { bad.Add(part); continue; }
                    if (module == null || module.Length == 0) { def = level; continue; }
                    if (module != Root && !module.StartsWith(Root + "::")) module = Root + "::" + module;
                    rules.Add((module, level));
                }
                lock (s_Resolved)
                {
                    s_Filter = value ?? "";
                    s_Rules = rules;
                    if (def.HasValue) s_Default = def.Value;
                    Rebuild();
                }
                if (bad.Count > 0) Warn("ignored log filter parts (use module=error|warn|info|debug|trace): " + string.Join(", ", bad));
            }
        }

        private static void Rebuild()
        {
            s_Resolved.Clear();
            var max = s_Default;
            foreach (var r in s_Rules) if (r.level > max) max = r.level;
            s_Max = max;
        }

        private static bool TryParseLevel(string s, out LogLevel level)
        {
            if (s.Equals("off", StringComparison.OrdinalIgnoreCase)) { level = (LogLevel)(-1); return true; }
            return Enum.TryParse(s, true, out level) && Enum.IsDefined(typeof(LogLevel), level);
        }

        /// <summary>Level in force for a target: longest matching rule prefix (on a "::" boundary), else the default.</summary>
        internal static LogLevel LevelFor(string target)
        {
            lock (s_Resolved)
            {
                if (s_Resolved.TryGetValue(target, out var l)) return l;
                int best = -1; l = s_Default;
                foreach (var (prefix, level) in s_Rules)
                    if (prefix.Length > best && (target == prefix || target.StartsWith(prefix + "::"))) { best = prefix.Length; l = level; }
                return s_Resolved[target] = l;
            }
        }

        /// <summary>True if some target logs at this level. For blocks that compute values just to log them.</summary>
        public static bool Enabled(LogLevel level) => level <= s_Max;
        /// <summary>True if the calling file's target logs at this level.</summary>
        public static bool EnabledHere(LogLevel level, [CallerFilePath] string file = "") => level <= s_Max && level <= LevelFor(Target(file));

        public static void Error(string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Error, msg, null, file, line);
        public static void Error(Exception ex, string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Error, msg, ex, file, line);
        public static void Warn(string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Warn, msg, null, file, line);
        public static void Warn(Exception ex, string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Warn, msg, ex, file, line);
        public static void Info(string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Info, msg, null, file, line);
        public static void Debug(string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Debug, msg, null, file, line);
        public static void Trace(string msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Write(LogLevel.Trace, msg, null, file, line);

        // Lazy overloads: the compiler prefers these for $"..." arguments; nothing is formatted when the level is off.
        public static void Error(ref ErrorMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Error, msg.B.Done(), null, file, line); }
        public static void Error(Exception ex, ref ErrorMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Error, msg.B.Done(), ex, file, line); }
        public static void Warn(ref WarnMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Warn, msg.B.Done(), null, file, line); }
        public static void Warn(Exception ex, ref WarnMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Warn, msg.B.Done(), ex, file, line); }
        public static void Info(ref InfoMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Info, msg.B.Done(), null, file, line); }
        public static void Debug(ref DebugMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Debug, msg.B.Done(), null, file, line); }
        public static void Trace(ref TraceMsg msg, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { if (msg.B.On) Write(LogLevel.Trace, msg.B.Done(), null, file, line); }

        private static void Write(LogLevel level, string msg, Exception ex, string file, int line)
        {
            if (level > s_Max) return;
            string target = Target(file);
            if (level > LevelFor(target)) return;
            string text = $"{target}:{line}: {msg}";
            if (ex != null) text += $"\n  caused by: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            try
            {
                switch (level)
                {
                    case LogLevel.Error: s_Log.Error(text); break;
                    case LogLevel.Warn: s_Log.Warn(text); break;
                    case LogLevel.Info: s_Log.Info(text); break;
                    case LogLevel.Debug: s_Log.Debug(text); break;
                    default: s_Log.Trace(text); break;
                }
            }
            catch { } // logging must never take the game down
        }

        /// <summary>"C:\...\Driving\NormalRoad.cs" -> "realistic_ems::driving::normal_road"; "HelicopterSystem.cs" -> "realistic_ems::helicopter".</summary>
        internal static string Target(string file)
        {
            lock (s_Targets)
            {
                if (s_Targets.TryGetValue(file, out var t)) return t;
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.EndsWith("System") && name.Length > 6) name = name.Substring(0, name.Length - 6);
                string dir = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
                var sb = new StringBuilder(Root + "::");
                if (dir.Length > 0 && dir != nameof(RealisticEMS)) sb.Append(Snake(dir)).Append("::");
                sb.Append(Snake(name));
                return s_Targets[file] = sb.ToString();
            }
        }

        internal static string Snake(string s)
        {
            var sb = new StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // break before an upper-case letter that starts a word: "NefDispatch" -> nef_dispatch, "DispatchUI" -> dispatch_ui
                if (char.IsUpper(c) && i > 0 && (char.IsLower(s[i - 1]) || i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        internal static void SelfCheck()
        {
            void Expect(bool ok, string what) { if (!ok) Error("log self-check failed: " + what); }
            Expect(Snake("NefDispatch") == "nef_dispatch", Snake("NefDispatch"));
            Expect(Snake("DispatchUI") == "dispatch_ui", Snake("DispatchUI"));
            Expect(Snake("Map3dPro") == "map3d_pro", Snake("Map3dPro"));
            Expect(Target(@"C:\x\RealisticEMS\HelicopterSystem.cs") == "realistic_ems::helicopter", "target helicopter");
            Expect(Target(@"C:\x\RealisticEMS\Driving\NormalRoad.cs") == "realistic_ems::driving::normal_road", "target driving::normal_road");

            // filter rules, restored afterwards
            var (oldDefault, oldFilter) = (s_Default, s_Filter);
            Filter = "warn,helicopter=trace,driving=debug,driving::siren=off";
            Expect(LevelFor("realistic_ems::helicopter") == LogLevel.Trace, "helicopter=trace");
            Expect(LevelFor("realistic_ems::helicopter_extra") == LogLevel.Warn, "prefix stops at ::");
            Expect(LevelFor("realistic_ems::driving::normal_road") == LogLevel.Debug, "driving=debug covers submodules");
            Expect(LevelFor("realistic_ems::driving::siren") < LogLevel.Error, "longest prefix wins (off)");
            Expect(LevelFor("realistic_ems::mod") == LogLevel.Warn, "bare level is the default");
            Expect(Enabled(LogLevel.Trace), "max level follows the most verbose rule");
            Filter = ""; MaxLevel = LogLevel.Info;
            int calls = 0;
            string Count() { calls++; return ""; }
            Trace($"lazy {Count()}");
            Expect(calls == 0, "disabled interpolation must not run");
            MaxLevel = oldDefault; Filter = oldFilter;
        }

        /// <summary>Shared body of the per-level handlers: formats only when the level can be on somewhere.</summary>
        public struct Builder
        {
            public bool On;
            private StringBuilder m_Sb;
            public Builder(int literalLength, LogLevel level, out bool enabled)
            {
                On = enabled = level <= s_Max;
                m_Sb = enabled ? new StringBuilder(literalLength + 32) : null;
            }
            public void Lit(string s) => m_Sb.Append(s);
            public void Fmt<T>(T value, string format, int alignment)
            {
                string s = format != null && value is IFormattable f ? f.ToString(format, null) : value?.ToString() ?? "";
                if (alignment > 0) s = s.PadLeft(alignment); else if (alignment < 0) s = s.PadRight(-alignment);
                m_Sb.Append(s);
            }
            public string Done() => m_Sb.ToString();
        }

        // One handler per level: the level must be known in the constructor, before any argument is evaluated.
        [InterpolatedStringHandler] public struct ErrorMsg { public Builder B; public ErrorMsg(int l, int n, out bool on) => B = new Builder(l, LogLevel.Error, out on); public void AppendLiteral(string s) => B.Lit(s); public void AppendFormatted<T>(T value) => B.Fmt(value, null, 0); public void AppendFormatted<T>(T value, string format) => B.Fmt(value, format, 0); public void AppendFormatted<T>(T value, int alignment, string format = null) => B.Fmt(value, format, alignment); }
        [InterpolatedStringHandler] public struct WarnMsg { public Builder B; public WarnMsg(int l, int n, out bool on) => B = new Builder(l, LogLevel.Warn, out on); public void AppendLiteral(string s) => B.Lit(s); public void AppendFormatted<T>(T value) => B.Fmt(value, null, 0); public void AppendFormatted<T>(T value, string format) => B.Fmt(value, format, 0); public void AppendFormatted<T>(T value, int alignment, string format = null) => B.Fmt(value, format, alignment); }
        [InterpolatedStringHandler] public struct InfoMsg { public Builder B; public InfoMsg(int l, int n, out bool on) => B = new Builder(l, LogLevel.Info, out on); public void AppendLiteral(string s) => B.Lit(s); public void AppendFormatted<T>(T value) => B.Fmt(value, null, 0); public void AppendFormatted<T>(T value, string format) => B.Fmt(value, format, 0); public void AppendFormatted<T>(T value, int alignment, string format = null) => B.Fmt(value, format, alignment); }
        [InterpolatedStringHandler] public struct DebugMsg { public Builder B; public DebugMsg(int l, int n, out bool on) => B = new Builder(l, LogLevel.Debug, out on); public void AppendLiteral(string s) => B.Lit(s); public void AppendFormatted<T>(T value) => B.Fmt(value, null, 0); public void AppendFormatted<T>(T value, string format) => B.Fmt(value, format, 0); public void AppendFormatted<T>(T value, int alignment, string format = null) => B.Fmt(value, format, alignment); }
        [InterpolatedStringHandler] public struct TraceMsg { public Builder B; public TraceMsg(int l, int n, out bool on) => B = new Builder(l, LogLevel.Trace, out on); public void AppendLiteral(string s) => B.Lit(s); public void AppendFormatted<T>(T value) => B.Fmt(value, null, 0); public void AppendFormatted<T>(T value, string format) => B.Fmt(value, format, 0); public void AppendFormatted<T>(T value, int alignment, string format = null) => B.Fmt(value, format, alignment); }
    }
}
