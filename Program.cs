using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Storage>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null); // bounded by the streaming upload handler
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddFixedWindowLimiter("uploads", options =>
    {
        options.PermitLimit = 30;
        options.Window = TimeSpan.FromMinutes(1);
        options.QueueLimit = 0;
    });
});
string publicUrl = builder.Configuration["PublicUrl"] ?? "http://localhost:8080";
if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http") || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
    throw new InvalidOperationException("PublicUrl must be an absolute HTTP(S) origin without a path.");
publicUrl = origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
var app = builder.Build();
_ = app.Services.GetRequiredService<Storage>();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/config", (Storage storage) => Results.Ok(new { maxFileBytes = storage.MaxBytes, requiresKey = !string.IsNullOrEmpty(storage.UploadKey) }));
app.MapPost("/api/files", async (HttpContext ctx, Storage storage, ILogger<Program> logger) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    // Browser uploads must originate from this site. Non-browser API clients can use the upload key.
    if (ctx.Request.Headers.TryGetValue("Origin", out var requestOrigin) && requestOrigin != publicUrl)
        return Results.Json(new { error = "Upload origin is not allowed." }, statusCode: 403);
    if (!storage.Authorized(ctx.Request.Headers["X-Upload-Key"].ToString()))
        return Results.Json(new { error = "The upload key is incorrect." }, statusCode: 401);
    if (ctx.Request.ContentLength > storage.MaxBytes)
        return Results.Json(new { error = "This file exceeds the upload limit." }, statusCode: 413);
    try
    {
        var file = await storage.Save(ctx.Request.Body, ctx.Request.Query["name"].ToString(), ctx.RequestAborted);
        return Results.Json(new
        {
            file.Name, file.Size, file.StoredSize, file.Compressed, file.Sha256,
            shareUrl = $"{publicUrl}/f/{file.Id}", downloadUrl = DownloadUrl(file)
        }, statusCode: 201);
    }
    catch (UploadException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
    catch (BadHttpRequestException) { return Results.Json(new { error = "The upload was interrupted or too large." }, statusCode: 400); }
    catch (IOException ex)
    {
        logger.LogError(ex, "Upload storage failure");
        return Results.Json(new { error = "Storage is unavailable. Please contact the host administrator." }, statusCode: 507);
    }
}).RequireRateLimiting("uploads");
app.MapMethods("/d/{id}/{name}", new[] { "GET", "HEAD" }, async (string id, HttpContext ctx, Storage storage) =>
{
    var file = await storage.Find(id, ctx.RequestAborted);
    if (file is null) return Results.NotFound();
    ctx.Response.ContentLength = file.Size;
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
    if (HttpMethods.IsHead(ctx.Request.Method))
    {
        ctx.Response.ContentType = "application/octet-stream";
        return Results.Empty;
    }
    return Results.Stream(storage.Open(file), "application/octet-stream", file.Name);
});
app.MapGet("/preview/{id}", async (string id, HttpContext ctx, Storage storage) =>
{
    var file = await storage.Find(id, ctx.RequestAborted);
    if (file is null || !file.Mime.StartsWith("image/") || file.Size > 20 * 1024 * 1024) return Results.NotFound();
    ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    ctx.Response.ContentLength = file.Size;
    return Results.Stream(storage.Open(file), file.Mime);
});
app.MapGet("/f/{id}", async (string id, HttpContext ctx, Storage storage) =>
{
    var f = await storage.Find(id, ctx.RequestAborted);
    if (f is null) return Results.Content(Page("File not found", "<h1>File not found</h1><p>This link is invalid or the file has been removed.</p><a href=\"/\">Return to uploads</a>", ""), "text/html", statusCode: 404);
    string share = $"{publicUrl}/f/{f.Id}";
    bool image = f.Mime.StartsWith("image/") && f.Size <= 20 * 1024 * 1024;
    string description = $"{FormatSize(f.Size)} · Download the original file from FileHarbor. No account needed.";
    string preview = image ? $"{publicUrl}/preview/{f.Id}" : $"{publicUrl}/embed.png";
    string meta = $"""
    <meta property="og:type" content="website">
    <meta property="og:site_name" content="FileHarbor">
    <meta property="og:title" content="{E(f.Name)}">
    <meta property="og:description" content="{E(description)}">
    <meta property="og:url" content="{E(share)}">
    <meta property="og:image" content="{E(preview)}">
    <meta name="twitter:card" content="summary_large_image">
    <meta name="theme-color" content="#2875a8">
    <meta name="description" content="{E(description)}">
    """;
    string content = $"""
    <p class="eyebrow">READY TO DOWNLOAD</p>
    <h1 class="filename">{E(f.Name)}</h1>
    <p>The file you need. Just one click away.</p>
    {(image ? $"<img class=\"file-preview\" src=\"/preview/{f.Id}\" alt=\"Preview of {E(f.Name)}\">" : "")}
    <fieldset><legend>File details</legend><dl class="details">
    <dt>Original size</dt><dd>{FormatSize(f.Size)}</dd>
    <dt>Uploaded</dt><dd>{f.CreatedAt:yyyy-MM-dd HH:mm} UTC</dd>
    <dt>Storage</dt><dd>{(f.Compressed ? $"Losslessly compressed · {100 * (1 - (double)f.StoredSize / f.Size):0}% smaller" : "Original · already compact")}</dd>
    <dt>SHA-256</dt><dd class="hash">{f.Sha256}</dd></dl>
    <p>Downloads preserve the original filename and bytes.</p>
    <a class="download-link" href="{E(DownloadUrl(f))}">Download {E(f.Name)}</a></fieldset>
    <fieldset><legend>Share this file</legend>
    <label for="share-link">Share page · includes a Discord preview</label>
    <div class="link-row"><input id="share-link" type="text" readonly value="{E(share)}"><button type="button" data-copy="share-link">Copy link</button></div>
    <label for="direct-link">Direct download</label>
    <div class="link-row"><input id="direct-link" type="text" readonly value="{E(DownloadUrl(f))}"><button type="button" data-copy="direct-link">Copy link</button></div>
    <p id="copy-status" role="status"></p></fieldset>
    <p>Only download files from people you trust. <a href="/">Upload your own file →</a></p>
    """;
    return Results.Content(Page(f.Name, content, meta), "text/html; charset=utf-8");
});
app.Run();

string DownloadUrl(StoredFile f) => $"{publicUrl}/d/{f.Id}/{Uri.EscapeDataString(f.Name)}";
static string E(string s) => WebUtility.HtmlEncode(s);
static string FormatSize(long n) => n >= 1048576 ? $"{n / 1048576d:0.##} MB" : n >= 1024 ? $"{n / 1024d:0.##} KB" : $"{n} bytes";
static string Page(string title, string content, string meta) => $"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{E(title)} · FileHarbor</title>{meta}<link rel="stylesheet" href="/vendor/7.css"><link rel="stylesheet" href="/site.css"><script src="/share.js" defer></script></head>
<body><header class="site-header"><nav class="shell"><a class="brand" href="/">FileHarbor</a><a href="/">Upload a file</a></nav></header>
<main class="shell share-page">{content}</main><footer class="shell">FileHarbor · Simple files. Easy sharing.</footer></body></html>
""";
