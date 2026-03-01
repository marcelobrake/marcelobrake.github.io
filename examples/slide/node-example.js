// npm i express pino @opentelemetry/api @opentelemetry/sdk-node
// npm i @opentelemetry/auto-instrumentations-node @opentelemetry/exporter-trace-otlp-http
// npm i @opentelemetry/exporter-metrics-otlp-http @opentelemetry/sdk-metrics

const express = require("express");
const pino = require("pino");

const { diag, DiagConsoleLogger, DiagLogLevel, trace, metrics, context } = require("@opentelemetry/api");
const { NodeSDK } = require("@opentelemetry/sdk-node");
const { getNodeAutoInstrumentations } = require("@opentelemetry/auto-instrumentations-node");
const { OTLPTraceExporter } = require("@opentelemetry/exporter-trace-otlp-http");
const { OTLPMetricExporter } = require("@opentelemetry/exporter-metrics-otlp-http");
const { PeriodicExportingMetricReader } = require("@opentelemetry/sdk-metrics");

diag.setLogger(new DiagConsoleLogger(), DiagLogLevel.ERROR);

// --- OTel bootstrap (trace + metrics) ---
const metricReader = new PeriodicExportingMetricReader({
    exporter: new OTLPMetricExporter({ url: "http://otel-collector:4318/v1/metrics" }),
    exportIntervalMillis: 5000,
});

const sdk = new NodeSDK({
    traceExporter: new OTLPTraceExporter({ url: "http://otel-collector:4318/v1/traces" }),
    instrumentations: [getNodeAutoInstrumentations()],
    metricReader,
});
sdk.start();

// --- JSONL logger ---
const log = pino({ level: "info" }); // default já sai JSON por linha (jsonl)

const app = express();
const meter = metrics.getMeter("demo");
const latency = meter.createHistogram("dependency_latency_ms", {
    description: "Latência de dependências (db/http/função)",
    unit: "ms",
});

app.get("/health", (req, res) => res.json({ ok: true }));

app.get("/checkout", async (req, res) => {
    const tracer = trace.getTracer("demo");
    await tracer.startActiveSpan("checkout", async (span) => {
        const started = Date.now();

        // span filho: função importante
        await tracer.startActiveSpan("calculate_total", (s) => {
            const t0 = Date.now();
            // ... lógica ...
            const dt = Date.now() - t0;
            latency.record(dt, { dep: "function", name: "calculate_total" });
            log.info({ event: "latency", dep: "function", name: "calculate_total", ms: dt, trace_id: span.spanContext().traceId });
            s.end();
        });

        // exemplo fake de “HTTP call”
        const t1 = Date.now();
        await new Promise((r) => setTimeout(r, 80));
        const httpMs = Date.now() - t1;
        latency.record(httpMs, { dep: "http", name: "payments_api" });
        span.setAttribute("dep.http.ms", httpMs);

        const totalMs = Date.now() - started;
        log.info({
            event: "request_done",
            route: "/checkout",
            status: 200,
            ms: totalMs,
            trace_id: span.spanContext().traceId,
        });

        span.end();
        res.json({ ok: true, total_ms: totalMs });
    });
});

app.listen(3000, () => log.info({ event: "boot", port: 3000 }));