using System.Collections.Concurrent;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://0.0.0.0:80");
builder.Services.AddSingleton<MockDeviceState>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
    await next();
});

app.MapGet("/health", () => Results.Json(new { status = "ready", device = "P12_Plus mock" }));
app.MapGet("/app/getthumbnail", (HttpContext context, MockDeviceState device) => device.GetThumbnail(context.Request.Query["file"].ToString()));
app.MapGet("/app/{endpoint}", (string endpoint, HttpContext context, MockDeviceState device) => device.Handle(endpoint, context));
app.MapGet("/{**path}", (string? path, MockDeviceState device) => device.GetFile(path));

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("Dashcam mock server ready.");
    Console.WriteLine("Enter this machine's IPv4 address in Dashcam Remote Control.");
    Console.WriteLine($"Media folder: {app.Services.GetRequiredService<MockDeviceState>().MediaFolder}");
    Console.WriteLine("Press Ctrl+C to stop.");
    foreach (var address in app.Urls)
    {
        Console.WriteLine($"Listening on {address}");
    }
});

await app.RunAsync();

internal sealed class MockDeviceState
{
    private readonly ConcurrentDictionary<string, int> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _recordingLock = new();
    private readonly object _filesLock = new();
    private readonly List<MockFile> _files;
    private DateTimeOffset? _recordingStartedAt;

    private static readonly MockSetting[] s_settings =
    [
        new("switchcam", ["Front camera", "Rear camera"], [0, 1], 0),
        new("resolution", ["4K", "2K", "1080P"], [0, 1, 2], 2),
        new("loopingtime", ["1 minute", "3 minutes", "5 minutes"], [1, 3, 5], 3),
        new("exposure", ["-2", "-1", "0", "+1", "+2"], [-2, -1, 0, 1, 2], 0),
        new("mic", ["Off", "On"], [0, 1], 1),
        new("wdr", ["Off", "On"], [0, 1], 1),
        new("parkingmode", ["Off", "Low", "High"], [0, 1, 2], 0)
    ];

    public static readonly byte[] Thumbnail = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/vkAAAAAASUVORK5CYII=");

    public MockDeviceState(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredFolder = configuration["MockDevice:MediaFolder"] ?? "Media";
        MediaFolder = Path.GetFullPath(Path.IsPathRooted(configuredFolder)
            ? configuredFolder
            : Path.Combine(environment.ContentRootPath, configuredFolder));
        Directory.CreateDirectory(MediaFolder);
        _files = LoadFiles(MediaFolder);

        foreach (var setting in s_settings)
        {
            _settings[setting.Name] = setting.DefaultValue;
        }
    }

    public string MediaFolder { get; }

    public IResult Handle(string endpoint, HttpContext context)
    {
        var query = context.Request.Query;
        var command = endpoint.ToLowerInvariant();

        return command switch
        {
            "getproductinfo" => Success(new { model = "P12_Plus", company = "Pelsee", soc = "SigmaStar SSC8629G" }),
            "getdeviceattr" => Success(new { softver = "V1.0.0-MOCK", ssid = "Pelsee_Mock", hwver = "P12_PLUS_MOCK", uuid = "MOCK-DEVICE-0001", camnum = 2, wifireboot = 0 }),
            "getsdinfo" => Success(new { status = 0, free = 28_000_000_000L, total = 64_000_000_000L }),
            "getbatteryinfo" => Success(new { capacity = "100%" }),
            "getrecduration" => Success(new { duration = GetRecordingDuration() }),
            "getmediainfo" => Success(new { rtsp = context.Request.Host.Host, port = 554 }),
            "getparamitems" => Success(GetSettings()),
            "getparamvalue" => Success(new { value = GetSetting(query["param"].ToString()) }),
            "setparamvalue" => SetSetting(query["param"].ToString(), query["value"].ToString()),
            "getfilelist" => Success(GetFileList(query["folder"].ToString())),
            "deletefile" => DeleteFile(query["file"].ToString()),
            "sdformat" => FormatCard(),
            "reset" => ResetSettings(),
            "enterrecorder" or "exitrecorder" or "setsystime" or "settimezone" or "setwifi" => Success(new { success = true }),
            _ => Success(new { success = true, endpoint })
        };
    }

    public IResult GetFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.NotFound();
        }

        var decodedPath = Uri.UnescapeDataString(path);
        var file = FindFile(decodedPath);
        if (file is null || !File.Exists(file.LocalPath))
        {
            return Results.NotFound(new { error = "Mock file not found." });
        }

        var contentType = Path.GetExtension(file.LocalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "video/mp2t";
        return Results.File(file.LocalPath, contentType, enableRangeProcessing: true);
    }

    public IResult GetThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.NotFound();
        }

        var decodedPath = Uri.UnescapeDataString(path);
        var file = FindFile(decodedPath);
        if (file is null || !File.Exists(file.LocalPath))
        {
            return Results.NotFound(new { error = "Mock file not found." });
        }

        return Path.GetExtension(file.LocalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            ? Results.File(file.LocalPath, "image/jpeg")
            : Results.Bytes(Thumbnail, "image/png");
    }

    private object GetSettings()
    {
        var values = s_settings.Select(setting => new
        {
            name = setting.Name,
            items = setting.Items,
            index = setting.Indexes
        });
        return values;
    }

    private int GetSetting(string name) => _settings.GetValueOrDefault(name, 0);

    private IResult SetSetting(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Success(new { success = true });
        }

        _settings[name] = parsed;
        if (!name.Equals("rec", StringComparison.OrdinalIgnoreCase))
        {
            return Success(new { success = true });
        }

        lock (_recordingLock)
        {
            _recordingStartedAt = parsed == 1 ? DateTimeOffset.UtcNow : null;
        }

        return Success(new { success = true });
    }

    private int GetRecordingDuration()
    {
        lock (_recordingLock)
        {
            return _recordingStartedAt is { } startedAt
                ? (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds
                : -1;
        }
    }

    private MockFolderListing[] GetFileList(string folder)
    {
        object[] files;
        lock (_filesLock)
        {
            files = _files
                .Where(file => file.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
                .Select(file => (object)new
                {
                    name = file.Name,
                    size = file.Size,
                    duration = file.Duration,
                    createtimestr = file.CreatedAt,
                    type = file.Type
                })
                .ToArray();
        }

        return [new MockFolderListing(files)];
    }

    private static List<MockFile> LoadFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var files = new List<MockFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(path);
            var relativePath = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var folderName = GetDeviceFolder(relativePath, extension);
            var type = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var duration = type == 1 ? 0 : -1;
            files.Add(new MockFile(
                folderName,
                $"/mnt/card/{relativePath}",
                info.Length,
                duration,
                info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                type,
                info.FullName));
        }

        return files;
    }

    private static string GetDeviceFolder(string relativePath, string extension)
    {
        var firstSegment = relativePath.Split('/')[0];
        if (firstSegment.Equals("loop", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("emr", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("event", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("park", StringComparison.OrdinalIgnoreCase))
        {
            return firstSegment.ToLowerInvariant();
        }

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? "event" : "loop";
    }

    private MockFile? FindFile(string path)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        lock (_filesLock)
        {
            return _files.FirstOrDefault(item => normalizedPath.EndsWith(item.Name.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        }
    }

    private IResult DeleteFile(string path)
    {
        var decodedPath = Uri.UnescapeDataString(path);
        lock (_filesLock)
        {
            _files.RemoveAll(file => decodedPath.EndsWith(file.Name, StringComparison.OrdinalIgnoreCase));
        }

        return Success(new { success = true });
    }

    private IResult FormatCard()
    {
        lock (_filesLock)
        {
            _files.Clear();
        }

        return Success(new { success = true });
    }

    private IResult ResetSettings()
    {
        foreach (var setting in s_settings)
        {
            _settings[setting.Name] = setting.DefaultValue;
        }

        return Success(new { success = true });
    }

    private static IResult Success(object? info) => Results.Json(new { result = 0, info });

    private sealed record MockFolderListing(object[] Files);

    private sealed record MockFile(string Folder, string Name, long Size, int Duration, string CreatedAt, int Type, string LocalPath);

    private sealed record MockSetting(string Name, string[] Items, int[] Indexes, int DefaultValue);
}
