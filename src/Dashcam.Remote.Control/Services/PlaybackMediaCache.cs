namespace Dashcam.Remote
{
    public sealed record PlaybackMediaCacheResult(string DirectoryPath, IReadOnlyList<Uri> MediaUris);

    public static class PlaybackMediaCache
    {
        private static readonly string s_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DashcamRemoteControl", "Playback"));

        public static async Task<PlaybackMediaCacheResult> DownloadAsync(
            DeviceApi api,
            IReadOnlyList<DeviceFile> files,
            Action<string, int>? reportProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (files.Count == 0)
            {
                throw new ArgumentException("Select at least one video to play.", nameof(files));
            }

            var directory = Path.Combine(s_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var localFiles = new List<Uri>(files.Count);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(file.Name);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        throw new InvalidDataException("The camera returned an invalid video filename.");
                    }

                    var destinationPath = Path.Combine(directory, fileName);
                    var relativePath = DeviceApi.EscapeFilePath(file.Name, false);
                    var lastPercent = -1;
                    reportProgress?.Invoke($"Preparing {fileName} for playback…", 0);
                    await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true))
                    {
                        await api.DownloadToAsync(relativePath, destination, (read, total) =>
                        {
                            var totalBytes = total is > 0 ? total.Value : file.Size;
                            if (totalBytes <= 0)
                            {
                                return;
                            }

                            var percent = (int)Math.Clamp(read * 100d / totalBytes, 0, 100);
                            var milestone = percent / 10 * 10;
                            if (milestone <= lastPercent)
                            {
                                return;
                            }

                            lastPercent = milestone;
                            reportProgress?.Invoke($"Downloading {fileName} for playback… {milestone}%", milestone);
                        }, cancellationToken);
                    }
                    localFiles.Add(new Uri(destinationPath));
                    reportProgress?.Invoke($"Ready to play {fileName}.", 100);
                }

                return new PlaybackMediaCacheResult(directory, localFiles);
            }
            catch
            {
                DeleteDirectory(directory);
                throw;
            }
        }

        public static void DeleteForMediaUris(IReadOnlyList<Uri> mediaUris)
        {
            var directories = mediaUris
                .Where(uri => uri.IsFile)
                .Select(uri => Path.GetFullPath(Path.GetDirectoryName(uri.LocalPath) ?? string.Empty))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (directories.Length != 1)
            {
                return;
            }

            DeleteDirectory(directories[0]);
        }

        private static void DeleteDirectory(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            var relative = Path.GetRelativePath(s_root, fullPath);
            if (relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return;
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
