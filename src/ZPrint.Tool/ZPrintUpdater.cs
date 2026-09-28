using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZPrint.Tool
{
    /// <summary>
    /// Information about a discovered remote release.
    /// </summary>
    public sealed class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public string? AssetName { get; set; }
        public string ReleaseNotes { get; set; } = string.Empty;
        public string? HtmlUrl { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Universal auto-updater integration bridge connecting ZPrint to ZUpdate engine.
    /// </summary>
    public static class ZPrintUpdater
    {
        private const string DefaultOwner = "kzxl";
        private const string DefaultRepo = "ZPrint";

        /// <summary>
        /// Queries GitHub Releases API to detect if a newer version of ZPrint is available.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
            string currentVersion,
            string owner = DefaultOwner,
            string repo = DefaultRepo,
            CancellationToken cancellationToken = default)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion
            };

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZPrint", currentVersion));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"GitHub API responded with {(int)response.StatusCode} ({response.ReasonPhrase})";
                    return result;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
                string cleanLatest = tagName.TrimStart('v', 'V');
                string cleanCurrent = currentVersion.TrimStart('v', 'V');

                result.LatestVersion = cleanLatest;
                result.ReleaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
                result.HtmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;

                // Compare versions
                if (Version.TryParse(cleanLatest, out var vLatest) && Version.TryParse(cleanCurrent, out var vCurrent))
                {
                    result.HasUpdate = vLatest > vCurrent;
                }
                else
                {
                    result.HasUpdate = !string.Equals(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase);
                }

                // Find ZIP asset
                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("zprint", StringComparison.OrdinalIgnoreCase))
                        {
                            result.AssetName = name;
                            result.DownloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                            break;
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Downloads the release package with progress reporting.
        /// </summary>
        public static async Task DownloadPackageAsync(
            string downloadUrl,
            string destinationZipPath,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var directory = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var fileStream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    int pct = (int)((double)totalRead / totalBytes * 100.0);
                    progress.Report(pct);
                }
            }
        }

        /// <summary>
        /// Locates ZUpdate.exe on the local machine.
        /// </summary>
        public static string? LocateZUpdateExecutable()
        {
            // 1. Same directory as zprint
            string localDir = AppContext.BaseDirectory;
            string localPath = Path.Combine(localDir, "ZUpdate.exe");
            if (File.Exists(localPath)) return localPath;

            // 2. LocalAppData standard installation
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroUniverse", "ZUpdate", "ZUpdate.exe");
            if (File.Exists(appData)) return appData;

            // 3. ZeroUniverse developer workspace build location
            string devWorkspacePath = @"E:\15. Other\ZeroUniverse\ZeroApps\ZUpdate\publish\zupdate-lite\ZUpdate.exe";
            if (File.Exists(devWorkspacePath)) return devWorkspacePath;

            return null;
        }

        /// <summary>
        /// Hands off the update process to ZUpdate.exe and terminates the current host process.
        /// </summary>
        public static bool HandOffToZUpdate(
            string packagePath,
            string targetVersion,
            string? sha256 = null,
            bool silent = false)
        {
            string? zUpdateExe = LocateZUpdateExecutable();
            if (string.IsNullOrEmpty(zUpdateExe) || !File.Exists(zUpdateExe))
            {
                return false;
            }

            string targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            int currentPid = Environment.ProcessId;
            string restartExe = "zprint.exe";

            string args = $"--app \"ZPrint\" " +
                          $"--pid {currentPid} " +
                          $"--target-dir \"{targetDir}\" " +
                          $"--package \"{packagePath}\" " +
                          $"--restart \"{restartExe}\" " +
                          $"--version \"{targetVersion}\"" +
                          (silent ? " --silent" : "") +
                          (string.IsNullOrEmpty(sha256) ? "" : $" --sha256 \"{sha256}\"");

            var psi = new ProcessStartInfo
            {
                FileName = zUpdateExe,
                Arguments = args,
                UseShellExecute = true
            };

            Process.Start(psi);
            Environment.Exit(0);
            return true;
        }
    }
}
