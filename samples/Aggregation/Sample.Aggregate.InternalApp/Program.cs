using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.HttpLogging;

var builder = WebApplication.CreateBuilder(args);

// --- Logging (Console) ---
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

// --- HTTP request/response logging (built-in) ---
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields =
        HttpLoggingFields.RequestPropertiesAndHeaders |
        HttpLoggingFields.RequestBody |
        HttpLoggingFields.ResponsePropertiesAndHeaders |
        HttpLoggingFields.ResponseBody;

    o.RequestBodyLogLimit = 64 * 1024;
    o.ResponseBodyLogLimit = 64 * 1024;

    // Optional: avoid logging very noisy headers
    // o.RequestHeaders.Remove("Authorization");
    // o.ResponseHeaders.Remove("Set-Cookie");
});

var app = builder.Build();

app.UseHttpLogging();

// --- Extra middleware: timing + correlation + exception logging ---
app.Use(async (ctx, next) =>
{
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestTrace");

    var id = ctx.TraceIdentifier;
    ctx.Response.Headers["x-trace-id"] = id;

    var sw = Stopwatch.StartNew();
    log.LogInformation("BEGIN {Method} {Path}{Query} from {RemoteIp}",
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.QueryString,
        ctx.Connection.RemoteIpAddress);

    try
    {
        await next();
        sw.Stop();

        log.LogInformation("END   {Method} {Path} -> {StatusCode} in {ElapsedMs}ms",
            ctx.Request.Method,
            ctx.Request.Path,
            ctx.Response.StatusCode,
            sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        sw.Stop();
        log.LogError(ex, "FAIL  {Method} {Path} after {ElapsedMs}ms",
            ctx.Request.Method,
            ctx.Request.Path,
            sw.ElapsedMilliseconds);
        throw;
    }
});

// --- Endpoints ---
app.MapGet("/", (HttpContext ctx, ILoggerFactory lf) =>
{
    lf.CreateLogger("Downstream").LogInformation("Handler: GET / (TraceId={TraceId})", ctx.TraceIdentifier);
    return "Hello from downstream on-prem app";
});

app.MapGet("/status", (HttpContext ctx, ILoggerFactory lf) =>
{
    lf.CreateLogger("Downstream").LogInformation("Handler: GET /status (TraceId={TraceId})", ctx.TraceIdentifier);
    return Results.Ok(new { name = "downstream-onprem", status = "ok", traceId = ctx.TraceIdentifier });
});

// --- Catch-all for seeing paths/headers/body easily ---
app.Map("/{**catchAll}", async (HttpContext ctx, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Downstream.CatchAll");

    string body = "";
    if (ctx.Request.ContentLength is > 0)
    {
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
    }

    log.LogWarning("CatchAll hit: {Method} {Path}{Query} ContentType={ContentType} BodyLen={BodyLen}",
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.QueryString,
        ctx.Request.ContentType,
        body.Length);

    // Echo back useful debug info
    return Results.Json(new
    {
        message = "CatchAll (debug)",
        method = ctx.Request.Method,
        path = ctx.Request.Path.Value,
        query = ctx.Request.QueryString.Value,
        traceId = ctx.TraceIdentifier,
        remoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
        headers = ctx.Request.Headers.ToDictionary(k => k.Key, v => v.Value.ToString()),
        body
    });
});

await app.RunAsync();
