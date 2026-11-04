using System.Net;
using System.Text.Json;

namespace Dashcam.Remote
{
    public sealed class DeviceApi : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
        private readonly HttpClient _downloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

        public string Host { get; private set; } = "192.168.169.1";

        public void SetHost(string value)
        {
            var host = value.Trim();
            if (Uri.TryCreate(host.Contains("://", StringComparison.Ordinal) ? host : $"http://{host}", UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Enter a device IP address or host name.");
            }

            Host = host;
        }

        public async Task<JsonElement> GetInfoAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            using var response = await GetAsync(new Uri($"http://{Host}/app/{endpoint}"), cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.GetInt32() == 0)
            {
                return root.TryGetProperty("info", out var payload) ? payload.Clone() : root.Clone();
            }

            var message = root.TryGetProperty("info", out var info) ? info.ToString() : "Unknown device error";
            throw new DeviceApiException(result.GetInt32(), message);

        }

        public async Task<byte[]> GetBytesAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            var uri = new Uri($"http://{Host}/{relativePath.TrimStart('/')}");
            using var response = await GetAsync(uri, cancellationToken);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        public async Task DownloadToAsync(string relativePath, Stream destination, Action<long, long?> progress, CancellationToken cancellationToken = default)
        {
            var uri = new Uri($"http://{Host}/{relativePath.TrimStart('/')}");
            try
            {
                // File transfers bypass the serialized API gate so bounded queue workers can run concurrently.
                using var response = await _downloadHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[64 * 1024];
                long bytesRead = 0;
                progress(0, totalBytes);
                while (true)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    bytesRead += count;
                    progress(bytesRead, totalBytes);
                }
            }
            catch (HttpRequestException exception)
            {
                throw new DeviceApiException(null, $"Could not download from http://{Host}: {exception.Message}", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DeviceApiException(null, $"Download from http://{Host} timed out.", exception);
            }
        }

        private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                var delay = TimeSpan.FromMilliseconds(180) - (DateTimeOffset.UtcNow - _lastRequest);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _lastRequest = DateTimeOffset.UtcNow;
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (HttpRequestException exception)
            {
                throw new DeviceApiException(null, $"Could not reach http://{Host}: {exception.Message}", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DeviceApiException(null, $"Request to http://{Host} timed out.", exception);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public static string QueryValue(string value) => WebUtility.UrlEncode(value);

        public static string EscapeFilePath(string path, bool leadingSlash)
        {
            var clean = path.TrimStart('/');
            var encoded = string.Join('/', clean.Split('/').Select(Uri.EscapeDataString));
            return leadingSlash ? $"/{encoded}" : encoded;
        }

        public void Dispose()
        {
            _http.Dispose();
            _downloadHttp.Dispose();
            _requestGate.Dispose();
        }
    }

    public sealed class DeviceApiException(int? resultCode, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public int? ResultCode { get; } = resultCode;
    }

    public sealed record DeviceFile(string Folder, string Name, long Size, int Duration, string CreatedAt, int Type = -1)
    {
        public string DisplayName => $"{Folder}  •  {Path.GetFileName(Name)}";
        public string RelativePath => Name.TrimStart('/');
        public override string ToString() => DisplayName;
    }

    public sealed record DeviceSetting(string Name, string[] Items, int[] Indexes, int? SelectedIndex);
}
