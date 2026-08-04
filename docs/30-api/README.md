# API contract

> **This directory is GENERATED. Never hand-edit it.**

The source of truth is `api/proto/wgagent/v1/*.proto` — see `REQ-API-001` in
[SPEC-04](../20-spec/SPEC-04-api-conventions.md).

## Regenerating

```bash
buf generate
```

Outputs:

| Output | Location |
|---|---|
| Go gRPC server and client | `gen/go/wgagent/v1/` |
| grpc-gateway (REST) | `gen/go/wgagent/v1/` |
| OpenAPI v3 specification | `docs/30-api/openapi.yaml` |
| Reference documentation | `docs/30-api/reference.md` |

Generated code is **committed** so builds do not depend on the network and contract changes are
visible in review.

## Compatibility checking

```bash
buf breaking --against '.git#branch=main'
```

CI blocks compatibility-breaking changes within a package version, per `REQ-API-061`.

## State

No `.proto` exists yet — authored during M0.
