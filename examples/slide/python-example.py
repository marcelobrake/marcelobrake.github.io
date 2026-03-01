# pip install fastapi uvicorn opentelemetry-api opentelemetry-sdk
# pip install opentelemetry-exporter-otlp opentelemetry-instrumentation-fastapi
# pip install opentelemetry-instrumentation-httpx

import json, time, logging
from fastapi import FastAPI
from opentelemetry import trace, metrics
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.exporter.otlp.proto.http.metric_exporter import OTLPMetricExporter
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor

# --- JSONL logger ---
log = logging.getLogger("demo")
log.setLevel(logging.INFO)
handler = logging.StreamHandler()
handler.setFormatter(logging.Formatter("%(message)s"))
log.addHandler(handler)

def jsonl(**fields):
  log.info(json.dumps(fields, ensure_ascii=False))

# --- OTel bootstrap ---
resource = Resource.create({"service.name": "demo-python"})

trace.set_tracer_provider(TracerProvider(resource=resource))
span_exporter = OTLPSpanExporter(endpoint="http://otel-collector:4318/v1/traces")
trace.get_tracer_provider().add_span_processor(BatchSpanProcessor(span_exporter))
tracer = trace.get_tracer("demo-python")

reader = PeriodicExportingMetricReader(
  OTLPMetricExporter(endpoint="http://otel-collector:4318/v1/metrics"),
  export_interval_millis=5000,
)
metrics.set_meter_provider(MeterProvider(resource=resource, metric_readers=[reader]))
meter = metrics.get_meter("demo-python")
latency = meter.create_histogram("dependency_latency_ms", unit="ms")

app = FastAPI()
FastAPIInstrumentor.instrument_app(app)  # auto-span HTTP server

@app.get("/checkout")
def checkout():
  start = time.time()
  with tracer.start_as_current_span("checkout") as span:
    # span filho: função importante
    with tracer.start_as_current_span("calculate_total") as child:
      t0 = time.time()
      time.sleep(0.01)
      ms = (time.time() - t0) * 1000
      latency.record(ms, {"dep": "function", "name": "calculate_total"})
      jsonl(event="latency", dep="function", name="calculate_total", ms=ms,
            trace_id=span.get_span_context().trace_id)

    # exemplo fake de “HTTP call”
    t1 = time.time()
    time.sleep(0.08)
    http_ms = (time.time() - t1) * 1000
    latency.record(http_ms, {"dep": "http", "name": "payments_api"})
    span.set_attribute("dep.http.ms", http_ms)

    total_ms = (time.time() - start) * 1000
    sc = span.get_span_context()
    jsonl(event="request_done", route="/checkout", status=200, ms=total_ms,
          trace_id=sc.trace_id, span_id=sc.span_id)

    return {"ok": True, "total_ms": total_ms}