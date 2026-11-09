using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RealisticEMS
{
    /// <summary>Copies the log to a new folder with a SHA-256 + ECDSA signature; verify with tools/verify-log.py and the public key.</summary>
    public static class LogExport
    {
        // ponytail: key is extractable from the DLL, so this only catches casual edits; move signing server-side if that matters.
        const string D = "yhcVUQ1TSZ9eCFxyvygbHvV79mWbbUx/adPMDBXO0SA=", X = "yfRX1L8NSWGETJ5W2vgyGNJjmCQsr4BBIInFBjKFMxc=", Y = "dST0qny7RS5F0CaIfqgAMaUWPQBGC5rzH2KmmKZdvOQ=";

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
                string msg = Mod.Version + "|" + ts + "|" + sha;

                string sig;
                using (var ec = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    D = Convert.FromBase64String(D),
                    Q = new ECPoint { X = Convert.FromBase64String(X), Y = Convert.FromBase64String(Y) },
                }))
                    sig = Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256));

                string dir = Path.Combine(logs, "RealisticEMS-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "RealisticEMS.log"), data);
                File.WriteAllText(Path.Combine(dir, "RealisticEMS.log.sig.json"),
                    "{\"version\":\"" + Mod.Version + "\",\"timestamp\":\"" + ts + "\",\"sha256\":\"" + sha + "\",\"signature\":\"" + sig + "\"}");
                Mod.log.Warn("Log exported to " + dir); // Warn so it shows even with debug logging off
            }
            catch (Exception e) { Mod.log.Warn("Log export failed: " + e.Message); }
        }
    }
}
