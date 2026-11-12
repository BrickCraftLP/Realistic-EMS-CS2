using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RealisticEMS
{
    /// <summary>Copies the log to a new folder with a SHA-256 manifest. No signing key ships in the DLL (it would be extractable), so this is not tamper-proof.</summary>
    public static class LogExport
    {
        public static void Run()
        {
            try
            {
                string logs = Path.Combine(Application.persistentDataPath, "Logs");
                byte[] data;
                using (var fs = new FileStream(Path.Combine(logs, "RealisticEMS.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream()) { fs.CopyTo(ms); data = ms.ToArray(); }

                string sha;
                using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                string ts = DateTime.UtcNow.ToString("o");

                string dir = Path.Combine(logs, "RealisticEMS-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "RealisticEMS.log"), data);
                File.WriteAllText(Path.Combine(dir, "RealisticEMS.log.sha256.json"),
                    "{\"version\":\"" + Mod.Version + "\",\"timestamp\":\"" + ts + "\",\"sha256\":\"" + sha + "\"}");
                Mod.log.Warn("Log exported to " + dir); // Warn so it shows even with debug logging off
            }
            catch (Exception e) { Mod.log.Warn("Log export failed: " + e.Message); }
        }
    }
}
