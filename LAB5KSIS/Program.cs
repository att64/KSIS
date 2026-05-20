var builder = WebApplication.CreateBuilder(args);

var storageRoot = Path.GetFullPath(
    builder.Configuration["StorageRoot"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "storage"));

Directory.CreateDirectory(storageRoot);

var app = builder.Build();

// ── Helpers ───────────────────────────────────────────────────────────────────

// Всегда читаем путь напрямую из Request.Path, чтобы избежать проблем
// с параметром маршрута /{**path} на плоских именах вроде /test.txt
string ResolveFromContext(HttpContext ctx)
{
    var raw = ctx.Request.Path.Value ?? "/";
    var relative = raw.TrimStart('/');
    if (string.IsNullOrEmpty(relative))
        throw new ArgumentException("Empty path.");
    var full = Path.GetFullPath(Path.Combine(storageRoot, relative));
    if (!full.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Path traversal.");
    return full;
}

string ResolveRaw(string raw)
{
    var relative = raw.TrimStart('/');
    if (string.IsNullOrEmpty(relative))
        throw new ArgumentException("Empty path.");
    var full = Path.GetFullPath(Path.Combine(storageRoot, relative));
    if (!full.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Path traversal.");
    return full;
}

static string GetMimeType(string filePath) =>
    Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".txt"  => "text/plain",
        ".html" => "text/html",
        ".htm"  => "text/html",
        ".css"  => "text/css",
        ".js"   => "application/javascript",
        ".json" => "application/json",
        ".xml"  => "application/xml",
        ".pdf"  => "application/pdf",
        ".png"  => "image/png",
        ".jpg"  => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".gif"  => "image/gif",
        ".svg"  => "image/svg+xml",
        ".zip"  => "application/zip",
        ".gz"   => "application/gzip",
        ".tar"  => "application/x-tar",
        ".mp4"  => "video/mp4",
        ".mp3"  => "audio/mpeg",
        _       => "application/octet-stream"
    };

// ── GET / — список корневой папки ────────────────────────────────────────────
app.MapGet("/", () =>
{
    var entries = new List<object>();
    foreach (var d in Directory.GetDirectories(storageRoot))
    {
        var di = new DirectoryInfo(d);
        entries.Add(new { name = di.Name, type = "directory", lastModified = di.LastWriteTimeUtc });
    }
    foreach (var f in Directory.GetFiles(storageRoot))
    {
        var fi = new FileInfo(f);
        entries.Add(new { name = fi.Name, type = "file", sizeBytes = fi.Length, lastModified = fi.LastWriteTimeUtc });
    }
    return Results.Ok(new { path = "/", entries });
});

// ── PUT /{**path} — загрузить или скопировать файл ───────────────────────────
app.MapPut("/{**path}", async (HttpContext ctx) =>
{
    string dest;
    try { dest = ResolveFromContext(ctx); }
    catch (ArgumentException) { return Results.BadRequest("Укажите путь к файлу, например /test.txt"); }
    catch { return Results.BadRequest("Недопустимый путь."); }

    // Режим копирования: заголовок X-Copy-From
    if (ctx.Request.Headers.TryGetValue("X-Copy-From", out var copyFrom) &&
        !string.IsNullOrWhiteSpace(copyFrom))
    {
        string src;
        try { src = ResolveRaw(copyFrom!); }
        catch { return Results.BadRequest("Недопустимый X-Copy-From путь."); }

        if (!File.Exists(src))
            return Results.NotFound($"Исходный файл не найден: {copyFrom}");

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);
        return Results.Ok(new { message = "Файл скопирован.", destination = ctx.Request.Path.Value });
    }

    // Обычная загрузка
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
    await ctx.Request.Body.CopyToAsync(fs);

    return Results.Ok(new { message = "Файл загружен.", path = ctx.Request.Path.Value });
});

// ── GET /{**path} — скачать файл или показать содержимое папки ───────────────
app.MapGet("/{**path}", (HttpContext ctx) =>
{
    string fsPath;
    try { fsPath = ResolveFromContext(ctx); }
    catch { return Results.BadRequest("Недопустимый путь."); }

    if (Directory.Exists(fsPath))
    {
        var entries = new List<object>();
        foreach (var d in Directory.GetDirectories(fsPath))
        {
            var di = new DirectoryInfo(d);
            entries.Add(new { name = di.Name, type = "directory", lastModified = di.LastWriteTimeUtc });
        }
        foreach (var f in Directory.GetFiles(fsPath))
        {
            var fi = new FileInfo(f);
            entries.Add(new { name = fi.Name, type = "file", sizeBytes = fi.Length, lastModified = fi.LastWriteTimeUtc });
        }
        return Results.Ok(new { path = ctx.Request.Path.Value, entries });
    }

    if (File.Exists(fsPath))
        return Results.File(fsPath, GetMimeType(fsPath), Path.GetFileName(fsPath), enableRangeProcessing: true);

    return Results.NotFound($"Файл или папка не найдены: {ctx.Request.Path.Value}");
});

// ── HEAD /{**path} — метаданные файла ────────────────────────────────────────
app.MapMethods("/{**path}", new[] { "HEAD" }, (HttpContext ctx) =>
{
    string fsPath;
    try { fsPath = ResolveFromContext(ctx); }
    catch { return Results.BadRequest("Недопустимый путь."); }

    if (!File.Exists(fsPath))
        return Results.NotFound();

    var fi = new FileInfo(fsPath);
    ctx.Response.Headers["X-File-Size"]     = fi.Length.ToString();
    ctx.Response.Headers["X-Last-Modified"] = fi.LastWriteTimeUtc.ToString("R");
    ctx.Response.Headers["Content-Length"]  = fi.Length.ToString();
    ctx.Response.Headers["Last-Modified"]   = fi.LastWriteTimeUtc.ToString("R");
    ctx.Response.Headers["Content-Type"]    = GetMimeType(fsPath);
    return Results.Ok();
});

// ── DELETE /{**path} — удалить файл или папку ────────────────────────────────
app.MapDelete("/{**path}", (HttpContext ctx) =>
{
    string fsPath;
    try { fsPath = ResolveFromContext(ctx); }
    catch (ArgumentException) { return Results.BadRequest("Укажите путь к файлу."); }
    catch { return Results.BadRequest("Недопустимый путь."); }

    if (File.Exists(fsPath))
    {
        File.Delete(fsPath);
        return Results.Ok(new { message = "Файл удалён.", path = ctx.Request.Path.Value });
    }
    if (Directory.Exists(fsPath))
    {
        Directory.Delete(fsPath, recursive: true);
        return Results.Ok(new { message = "Папка удалена.", path = ctx.Request.Path.Value });
    }

    return Results.NotFound($"Файл или папка не найдены: {ctx.Request.Path.Value}");
});

app.Run();
