// dotnet add package OpenTelemetry.Extensions.Hosting
// dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
// dotnet add package OpenTelemetry.Instrumentation.AspNetCore
// dotnet add package OpenTelemetry.Instrumentation.Http
// dotnet add package OpenTelemetry.Instrumentation.Runtime
// dotnet add package OpenTelemetry.Instrumentation.Process

using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var serviceName = "demo-dotnet";
var activitySource = new ActivitySource(serviceName);

builder.Services.AddOpenTelemetry()
  .ConfigureResource(r => r.AddService(serviceName))
  .WithTracing(t => t
    .AddSource(serviceName)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317")))
  .WithMetrics(m => m
    .AddMeter(serviceName)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317")));

var app = builder.Build();

var meter = new Meter(serviceName);
var latency = meter.CreateHistogram<double>("dependency_latency_ms", unit: "ms");

static void JsonlLog(object obj) =>
  Console.WriteLine(JsonSerializer.Serialize(obj)); // 1 JSON por linha (JSONL)

app.MapGet("/checkout", async (HttpContext ctx) =>
{
    using var activity = activitySource.StartActivity("checkout", ActivityKind.Server);

    var sw = Stopwatch.StartNew();

    // span filho: função importante
    using (var child = activitySource.StartActivity("calculate_total", ActivityKind.Internal))
    {
        var t0 = Stopwatch.StartNew();
        // ... lógica ...
        await Task.Delay(10);
        t0.Stop();

        latency.Record(t0.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("dep", "function"));
        JsonlLog(new
        {
            @event = "latency",
            dep = "function",
            name = "calculate_total",
            ms = t0.Elapsed.TotalMilliseconds,
            trace_id = Activity.Current?.TraceId.ToString()
        });
    }

    // exemplo fake de “HTTP call”
    var httpSw = Stopwatch.StartNew();
    await Task.Delay(80);
    httpSw.Stop();

    latency.Record(httpSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("dep", "http"));
    activity?.SetTag("dep.http.ms", httpSw.Elapsed.TotalMilliseconds);

    sw.Stop();

    JsonlLog(new
    {
        @event = "request_done",
        route = "/checkout",
        status = 200,
        ms = sw.Elapsed.TotalMilliseconds,
        trace_id = Activity.Current?.TraceId.ToString(),
        span_id = Activity.Current?.SpanId.ToString()
    });

    return Results.Ok(new { ok = true, total_ms = sw.Elapsed.TotalMilliseconds });
});

app.Run();