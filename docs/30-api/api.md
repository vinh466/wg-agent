# API contract

<!-- GENERATED from api/openapi.yaml by docs/render-api.py — REQ-API-060. Never hand-edit. -->

**wg-agent 1.0** — REST over `/v1`, one bearer token (ADR-0015).

See [SPEC-04](../20-spec/SPEC-04-api-conventions.md) for the conventions and the error model,
and [api/openapi.yaml](../../api/openapi.yaml) for the machine-readable contract.

## Operations

| Method | Path | Operation | Summary |
|---|---|---|---|
| `GET` | `/v1/interfaces` | ListInterfaces | List the interfaces the agent manages. |
| `POST` | `/v1/interfaces` | CreateInterface | Create an interface the agent manages. |
| `GET` | `/v1/interfaces/{name}` | GetInterface | Show an interface, its spec and its status. |
| `PUT` | `/v1/interfaces/{name}` | UpdateInterface | Replace the whole spec of an interface (REQ-API-064); its hooks are left as the CLI set them (REQ-API-085). |
| `DELETE` | `/v1/interfaces/{name}` | DeleteInterface | Delete an interface, its file and its peers. |
| `GET` | `/v1/interfaces/{name}/peers` | ListPeers | List the peers of an interface, each with its status. |
| `POST` | `/v1/interfaces/{name}/peers` | CreatePeer | Add a peer — a supplied public key, or a key pair the agent generates. |
| `GET` | `/v1/interfaces/{name}/peers/{public_key}` | GetPeer | Show a peer, its spec and its status. |
| `PUT` | `/v1/interfaces/{name}/peers/{public_key}` | UpdatePeer | Replace the whole spec of a peer (REQ-API-064). |
| `DELETE` | `/v1/interfaces/{name}/peers/{public_key}` | DeletePeer | Remove a peer from an interface. |
| `GET` | `/v1/version` | GetVersion | The agent version, the commit, the process start time and its uptime (REQ-API-078). |
| `GET` | `/v1/health` | GetHealth | Whether every startup check has passed (REQ-API-051). Served without a token (REQ-SEC-080). |

## Schemas

### CreateInterfaceRequest

The desired state of a new interface. No post_up or post_down: hooks are the CLI's alone (REQ-API-084, REQ-SEC-040).

| Field | Type | Required | Notes |
|---|---|---|---|
| `name` | string | yes | The interface's name (REQ-RES-011); its identity, immutable after creation. |
| `private_key` | string | no | Standard base64 of 32 bytes; generated when omitted (REQ-KEY-001). Write-only (REQ-RES-013). |
| `listen_port` | integer | no |  |
| `addresses` | array of string | yes | The interface's addresses, as CIDRs (IPv4 only). |
| `mtu` | integer | no |  |
| `enabled` | boolean | no |  |
| `labels` | object | no |  |

### UpdateInterfaceRequest

Replaces the whole spec (REQ-API-064). An omitted listen_port, mtu or enabled takes its default (REQ-API-075, REQ-API-076); an omitted private_key keeps the stored one (REQ-API-065).

| Field | Type | Required | Notes |
|---|---|---|---|
| `name` | string | no | If given, must equal the path; a differing value is rejected with FIELD_IMMUTABLE (REQ-API-066). |
| `private_key` | string | no |  |
| `listen_port` | integer | no |  |
| `addresses` | array of string | yes |  |
| `mtu` | integer | no |  |
| `enabled` | boolean | no |  |
| `labels` | object | no |  |

### InterfaceSpecView

The readable spec of an interface. No private_key (REQ-RES-013), no hooks (REQ-API-084).

| Field | Type | Required | Notes |
|---|---|---|---|
| `listen_port` | integer | no |  |
| `addresses` | array of string | no |  |
| `mtu` | integer | no |  |
| `enabled` | boolean | no |  |
| `labels` | object | no |  |

### InterfaceResource

An interface, its identity outside spec and status (REQ-RES-034).

| Field | Type | Required | Notes |
|---|---|---|---|
| `name` | string | yes |  |
| `spec` | [InterfaceSpecView](#interfacespecview) | no |  |
| `status` | [InterfaceStatus](#interfacestatus) | yes |  |

### InterfaceStatus

| Field | Type | Required | Notes |
|---|---|---|---|
| `public_key` | string? | no |  |
| `listen_port` | integer? | no |  |
| `created_at` | string? | no |  |
| `oper_state` | [OperState](#operstate) | yes |  |
| `peer_count` | integer? | no |  |
| `warnings` | array of [Warning](#warning) | yes |  |

### InterfaceWriteResult

| Field | Type | Required | Notes |
|---|---|---|---|
| `interface` | [InterfaceResource](#interfaceresource) | yes |  |
| `restarted` | boolean | yes | Whether the write restarted the unit, interrupting the interface's sessions (REQ-APL-007). |

### CreatePeerRequest

A peer with a supplied public key, or a key pair the agent generates. The client configuration fields shape only the file a generated peer returns.

| Field | Type | Required | Notes |
|---|---|---|---|
| `public_key` | string | no | Standard base64 of 32 bytes (REQ-RES-027). Omitted when generate_keypair is true. |
| `generate_keypair` | boolean | no | Generate the peer's key pair and return its private key and client configuration once. |
| `generate_preshared_key` | boolean | no |  |
| `preshared_key` | string | no | Standard base64 of 32 bytes. Write-only (REQ-RES-022). |
| `allowed_ips` | array of string | no |  |
| `endpoint` | string | no |  |
| `persistent_keepalive` | integer | no |  |
| `labels` | object | no |  |
| `client_allowed_ips` | array of string | no | The AllowedIPs of the returned client configuration (REQ-KEY-044). |
| `client_persistent_keepalive` | integer | no | The client configuration's keepalive; 25 when omitted, omitted at 0 (REQ-KEY-046). |
| `dns` | array of string | no | DNS servers written into the client configuration only (REQ-KEY-034). |
| `node_endpoint` | string | no | The host clients reach this node at; the configured node.endpoint when omitted (REQ-KEY-037). |

### UpdatePeerRequest

Replaces the whole spec (REQ-API-064). An omitted preshared_key keeps the stored one (REQ-API-065).

| Field | Type | Required | Notes |
|---|---|---|---|
| `public_key` | string | no | If given, must equal the path; a differing value is rejected with FIELD_IMMUTABLE (REQ-API-066). |
| `preshared_key` | string | no |  |
| `allowed_ips` | array of string | yes |  |
| `endpoint` | string | no |  |
| `persistent_keepalive` | integer | no |  |
| `labels` | object | no |  |

### PeerSpecView

The readable spec of a peer. No preshared_key (REQ-RES-022).

| Field | Type | Required | Notes |
|---|---|---|---|
| `allowed_ips` | array of string | no |  |
| `endpoint` | string | no |  |
| `persistent_keepalive` | integer | no |  |
| `labels` | object | no |  |

### PeerResource

A peer, identified by its interface and public key (REQ-RES-020).

| Field | Type | Required | Notes |
|---|---|---|---|
| `interface_name` | string | yes |  |
| `public_key` | string | yes |  |
| `spec` | [PeerSpecView](#peerspecview) | yes |  |
| `status` | [PeerStatus](#peerstatus) | yes |  |

### PeerStatus

| Field | Type | Required | Notes |
|---|---|---|---|
| `last_handshake_at` | string? | no |  |
| `handshake_age_seconds` | integer? | no |  |
| `online` | boolean | yes |  |
| `rx_bytes` | integer? | no |  |
| `tx_bytes` | integer? | no |  |
| `resolved_endpoint` | string? | no |  |
| `warnings` | array of [Warning](#warning) | yes |  |

### PeerWriteResult

| Field | Type | Required | Notes |
|---|---|---|---|
| `peer` | [PeerResource](#peerresource) | yes |  |
| `restarted` | boolean | yes |  |

### CreatePeerResult

| Field | Type | Required | Notes |
|---|---|---|---|
| `peer` | [PeerResource](#peerresource) | yes |  |
| `restarted` | boolean | yes |  |
| `private_key` | string | no | The generated private key, returned once (REQ-KEY-011). Absent for a supplied key. |
| `preshared_key` | string | no | The generated preshared key, returned once (REQ-KEY-021). Absent when none was generated. |
| `client_configuration` | string | no | A wg-quick .conf for the client, built from the generated key (REQ-KEY-042). |

### Warning

One validation finding of warning severity (REQ-RES-033).

| Field | Type | Required | Notes |
|---|---|---|---|
| `code` | [ReasonCode](#reasoncode) | yes |  |
| `message` | string | yes |  |

### OperState

REQ-RES-035.

One of: `UP`, `DOWN`, `ABSENT`.

### VersionResponse

| Field | Type | Required | Notes |
|---|---|---|---|
| `version` | string | yes |  |
| `commit` | string | yes |  |
| `started_at` | string | yes |  |
| `uptime_seconds` | integer | yes |  |

### HealthResponse

| Field | Type | Required | Notes |
|---|---|---|---|
| `status` | string (ok) | yes |  |

### Problem

An RFC 9457 problem document. Clients branch on `reason`, never on the message (REQ-API-082, REQ-API-041).

| Field | Type | Required | Notes |
|---|---|---|---|
| `type` | string | no |  |
| `title` | string | no |  |
| `status` | integer | no |  |
| `detail` | string | no |  |
| `reason` | [ReasonCode](#reasoncode) | yes |  |

### ReasonCode

The closed set of reason codes this build produces (SPEC-04 section 7.1).

One of: `INTERFACE_NAME_INVALID`, `INTERFACE_NOT_MANAGED`, `INTERFACE_EXISTS`, `LISTEN_PORT_IN_USE`, `ADDRESS_CONFLICT`, `PEER_NOT_FOUND`, `PEER_EXISTS`, `PUBLIC_KEY_INVALID`, `ALLOWED_IPS_DUPLICATE`, `ALLOWED_IPS_OVERLAP`, `ALLOWED_IPS_OUT_OF_SUBNET`, `TOKEN_INVALID`, `IPV6_NOT_SUPPORTED`, `APPLY_FAILED`, `FIELD_IMMUTABLE`, `FIELD_UNKNOWN`, `ADDRESSES_REQUIRED`, `ALLOWED_IPS_REQUIRED`, `ENDPOINT_REQUIRED`, `MTU_OUT_OF_RANGE`, `ENDPOINT_NOT_IP`, `LISTEN_PORT_INVALID`, `KEEPALIVE_INVALID`, `MTU_INVALID`, `KEY_INVALID`, `PEER_IS_INTERFACE`, `ALLOWED_IPS_NOT_CANONICAL`, `ENDPOINT_INVALID`, `CLIENT_ADDRESS_MISSING`, `ALLOWED_IPS_DEFAULT_ROUTE`, `HOOK_INVALID`, `SUBNET_FULL`, `CIDR_INVALID`, `STORE_BUSY`.

