#!/usr/bin/env python3
"""Render docs/30-api/api.md from api/openapi.yaml — REQ-API-060.

The OpenAPI document is the source of truth (REQ-API-001); this rendering is generated, never
hand-edited. Run it after any change to the contract; docs/check-docs.sh asserts it is in sync.
"""
import sys, pathlib
try:
    import yaml
except ImportError:
    sys.exit("render-api: PyYAML is required (pip install pyyaml).")

ROOT = pathlib.Path(__file__).resolve().parent.parent
doc = yaml.safe_load((ROOT / "api/openapi.yaml").read_text())

def ref_name(schema):
    return schema["$ref"].split("/")[-1] if "$ref" in schema else None

def type_of(schema):
    if schema is None:
        return ""
    if (name := ref_name(schema)):
        return f"[{name}](#{name.lower()})"
    t = schema.get("type", "")
    if isinstance(t, list):
        t = " or ".join(x for x in t if x != "null")
        if "null" in schema.get("type", []):
            t += "?"
    if t == "array":
        return f"array of {type_of(schema.get('items'))}"
    if schema.get("enum"):
        return t + " (" + ", ".join(schema["enum"]) + ")"
    return t

lines = [
    "# API contract",
    "",
    "<!-- GENERATED from api/openapi.yaml by docs/render-api.py — REQ-API-060. Never hand-edit. -->",
    "",
    f"**{doc['info']['title']} {doc['info']['version']}** — REST over `/v1`, one bearer token (ADR-0015).",
    "",
    "See [SPEC-04](../20-spec/SPEC-04-api-conventions.md) for the conventions and the error model,",
    "and [api/openapi.yaml](../../api/openapi.yaml) for the machine-readable contract.",
    "",
    "## Operations",
    "",
    "| Method | Path | Operation | Summary |",
    "|---|---|---|---|",
]
methods = ("get", "post", "put", "delete")
rows = []
for path, item in doc["paths"].items():
    for method in methods:
        if method in item:
            op = item[method]
            summary = op.get("summary", "").strip()
            rows.append((op["operationId"], method.upper(), path, summary))
for op_id, method, path, summary in rows:
    lines.append(f"| `{method}` | `/v1{path}` | {op_id} | {summary} |")

lines += ["", "## Schemas", ""]
for name, schema in doc["components"]["schemas"].items():
    lines.append(f"### {name}")
    lines.append("")
    if (desc := schema.get("description")):
        lines.append(" ".join(desc.split()))
        lines.append("")
    if schema.get("enum") and "properties" not in schema:
        lines.append("One of: " + ", ".join(f"`{v}`" for v in schema["enum"]) + ".")
        lines.append("")
        continue
    required = set(schema.get("required", []))
    props = schema.get("properties")
    if props:
        lines.append("| Field | Type | Required | Notes |")
        lines.append("|---|---|---|---|")
        for field, sub in props.items():
            note = " ".join(sub.get("description", "").split())
            lines.append(f"| `{field}` | {type_of(sub)} | {'yes' if field in required else 'no'} | {note} |")
        lines.append("")

out = ROOT / "docs/30-api/api.md"
out.write_text("\n".join(lines) + "\n")
print(f"render-api: wrote {out.relative_to(ROOT)} — {len(rows)} operations, {len(doc['components']['schemas'])} schemas")
