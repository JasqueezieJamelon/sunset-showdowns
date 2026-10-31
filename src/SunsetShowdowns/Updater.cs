using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LLD
{
    public enum UpdateState { Off, Checking, UpToDate, Available, Downloading, Verifying, Ready, Failed }

    public class UpdateInfo
    {
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
        public string Notes = "";
    }

    /// <summary>
    /// Checks a release feed on boot and, if the player asks for it, downloads the new build and
    /// swaps itself in. A running exe cannot overwrite itself on Windows, so the newly downloaded
    /// one does the swap: it waits for this process to exit, copies itself over the install, and
    /// starts it again.
    /// </summary>
    public static class Updater
    {
        public static volatile UpdateState State = UpdateState.Off;
        public static UpdateInfo Found;
        public static string Message = "";
        public static float Progress;          // 0..1 while downloading

        /// Default feed. A GitHub releases API url or a small json manifest both work;
        /// override it with `updateUrl=` in settings.ini.
        public const string DefaultFeed =
            "https://api.github.com/repos/JasqueezieJamelon/sunset-showdowns/releases/latest";

        static string TempDir => Path.Combine(Path.GetTempPath(), "SunsetShowdowns-update");

        public static string ExePath
        {
            get
            {
                try { return Environment.ProcessPath ?? ""; } catch { return ""; }
            }
        }

        // ---------- the swap, run by the freshly downloaded build ----------
        /// <summary>
        /// Entry point for `--apply-update &lt;target exe&gt; &lt;pid&gt;`. Runs headless.
        /// </summary>
        public static int ApplyUpdate(string[] args)
        {
            if (args.Length < 3) return 2;
            string target = args[1];
            int pid = int.TryParse(args[2], out int p) ? p : -1;

            // wait for the old build to let go of the file
            if (pid > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.WaitForExit(20000);
                }
                catch { /* already gone */ }
            }

            string me = ExePath;
            string backup = target + ".bak";
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                        try { File.Move(target, backup); } catch { }
                    }
                    File.Copy(me, target, true);
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target) ?? "." });
                    return 0;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }

            // could not replace it: put the old one back so the player is not left with nothing
            try { if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target); } catch { }
            return 1;
        }

        // ---------- checking ----------
        public static void CheckInBackground(string feed)
        {
            if (string.IsNullOrWhiteSpace(feed)) { State = UpdateState.Off; return; }
            State = UpdateState.Checking;
            Task.Run(async () =>
            {
                try
                {
                    CleanLeftovers();
                    var info = await Fetch(feed);
                    if (info == null || string.IsNullOrEmpty(info.Version))
                    {
                        State = UpdateState.UpToDate;
                        return;
                    }
                    if (Compare(info.Version, App.Version) > 0 && !string.IsNullOrEmpty(info.Url))
                    {
                        Found = info;
                        Message = info.Version;
                        State = UpdateState.Available;
                    }
                    else State = UpdateState.UpToDate;
                }
                catch (Exception e)
                {
                    Message = Short(e.Message);
                    State = UpdateState.Failed;
                }
            });
        }

        static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // GitHub refuses requests without one
            c.DefaultRequestHeaders.Add("User-Agent", "SunsetShowdowns/" + App.Version);
            c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return c;
        }

        static async Task<UpdateInfo> Fetch(string feed)
        {
            using var http = MakeClient();
            string json = await http.GetStringAsync(feed);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // a github release
            if (root.TryGetProperty("tag_name", out var tag))
            {
                var info = new UpdateInfo { Version = (tag.GetString() ?? "").TrimStart('v', 'V') };
                if (root.TryGetProperty("body", out var body)) info.Notes = body.GetString() ?? "";
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (a.TryGetProperty("browser_download_url", out var u)) info.Url = u.GetString() ?? "";
                        break;
                    }
                }
                return info;
            }

            // a plain manifest
            var man = new UpdateInfo();
            if (root.TryGetProperty("version", out var v)) man.Version = (v.GetString() ?? "").TrimStart('v', 'V');
            if (root.TryGetProperty("url", out var url)) man.Url = url.GetString() ?? "";
            if (root.TryGetProperty("sha256", out var sh)) man.Sha256 = sh.GetString() ?? "";
            if (root.TryGetProperty("notes", out var nt)) man.Notes = nt.GetString() ?? "";
            return man;
        }

        /// <summary>Compares dotted versions. Positive when a is newer than b.</summary>
        public static int Compare(string a, string b)
        {
            string[] pa = (a ?? "").Split('.'), pb = (b ?? "").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length && int.TryParse(pa[i], out int xi) ? xi : 0;
                int y = i < pb.Length && int.TryParse(pb[i], out int yi) ? yi : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        // ---------- downloading ----------
        /// True once the player has actually asked for the update, so a quiet background
        /// failure never nags someone who is just trying to play.
        public static volatile bool Asked;

        public static void DownloadAndApply()
        {
            if (Found == null || State == UpdateState.Downloading) return;
            Asked = true;
            State = UpdateState.Downloading;
            Progress = 0f;

            Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(TempDir);
                    string dest = Path.Combine(TempDir, "SunsetShowdowns-" + Found.Version + ".exe");

                    using (var http = MakeClient())
                    using (var resp = await http.GetAsync(Found.Url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? 0;
                        using var src = await resp.Content.ReadAsStreamAsync();
                        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                        var buffer = new byte[128 * 1024];
                        long done = 0;
                        int read;
                        while ((read = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, read);
                            done += read;
                            Progress = total > 0 ? (float)done / total : 0f;
                        }
                    }

                    if (!string.IsNullOrEmpty(Found.Sha256))
                    {
                        State = UpdateState.Verifying;
                        if (!HashMatches(dest, Found.Sha256))
                        {
                            try { File.Delete(dest); } catch { }
                            Message = "the download did not match its checksum";
                            State = UpdateState.Failed;
                            return;
                        }
                    }

                    PendingExe = dest;
                    State = UpdateState.Ready;
                }
                catch (Exception e)
                {
                    Message = Short(e.Message);
                    State = UpdateState.Failed;
                }
            });
        }

        public static string PendingExe = "";

        static bool HashMatches(string file, string expect)
        {
            using var fs = File.OpenRead(file);
            using var sha = SHA256.Create();
            string got = Convert.ToHexString(sha.ComputeHash(fs));
            return string.Equals(got, expect.Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Hands over to the downloaded build and quits. Does not return on success.</summary>
        public static bool Handover()
        {
            try
            {
                string target = ExePath;
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(PendingExe)) return false;
                var psi = new ProcessStartInfo(PendingExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = TempDir,
                };
                psi.ArgumentList.Add("--apply-update");
                psi.ArgumentList.Add(target);
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi);
                return true;
            }
            catch (Exception e)
            {
                Message = Short(e.Message);
                State = UpdateState.Failed;
                return false;
            }
        }

        static void CleanLeftovers()
        {
            try
            {
                if (!Directory.Exists(TempDir)) return;
                foreach (var f in Directory.GetFiles(TempDir, "*.exe"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "something went wrong";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 70 ? s.Substring(0, 70) : s;
        }
    }
}
