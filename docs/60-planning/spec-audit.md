---
updated: 2026-09-29
---

# Specification audit, September 2026

A read of all twelve modules before the implementation is rebuilt from them — next action 1 of
the [roadmap](roadmap.md). Nothing here is normative. Each item either becomes a spec change
through the workflow in `CLAUDE.md`, or is closed with a reason recorded against it.

The wrapper direction of [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md),
decided after this audit, moves most modules to the backlog. Each item is reassessed with its
module when the v1 specification is cut; an item in a deferred module waits with it.

Items are of three kinds: **D** needs a product decision, **M** has an evident fix, **P** is
planning or non-spec documentation.

## 1. Method

| Pass | Scope | Outcome |
|---|---|---|
| Provenance | Every live requirement traced through history; commits that also changed code flagged | 314 live, 11 struck. Introduced alongside code: `REQ-CFG-042` (800460b), `REQ-CLI-008` and `REQ-CLI-009` (1dd2366) — content reviewed, kept. Amendments in 2306fdf, 0cdb96c and efef142 follow spec review, kernel facts and domain discovery — kept |
| Wording | Requirement text naming a language, library, tool or an excluded architecture | M-01 |
| Backlog | Deferred and kept IDs against the spec | P-01 |
| Semantic | Every module read in full; suspected kernel behaviour probed | sections 2 to 4 |

Probes ran in a container on kernel 7.0 with `CAP_NET_ADMIN`. **Verified** marks behaviour
observed there, not inferred.

## 2. Kernel facts established by probe

| # | Observed | Bears on |
|---|---|---|
| K-1 | Writing `listen-port 0` to an up device rebinds it to a new random port (40484, then 56023) | D-04 |
| K-2 | The kernel clamps a private key: `////…8=` reads back as `+P//…38=` | D-03 |
| K-3 | `allowed-ips 10.9.0.5/24` reads back as `10.9.0.0/24`; a route with host bits is refused `EINVAL` | D-03 |
| K-4 | A peer carrying the device's own public key: the set call succeeds, the peer never appears | M-13 |
| K-5 | A route identical to the kernel prefix route of an address is refused `EEXIST`; a differing metric is accepted | D-05 |
| K-6 | A WireGuard link accepts MTU 0 and 65536. MTU 67 deletes every IPv4 address, re-adding fails `ENOBUFS`, and restoring 1420 does not bring them back | M-14 |
| K-7 | Link names `all` and `default` are refused `EINVAL` | M-15 |
| K-8 | An up WireGuard link carries no IPv6 link-local address (`addr_gen_mode = 1`) | closes a suspected adoption failure |
| K-9 | The NativeAOT binary requires `GLIBC_2.34` when built on glibc 2.39, and runs under `MemoryDenyWriteExecute=yes` | D-10 |
| K-10 | Under each distribution's own systemd as PID 1 (252, 255, 257, 259), a unit with `User=nobody`, `AmbientCapabilities=CAP_NET_ADMIN`, `ProtectKernelTunables=yes` and `ReadWritePaths=/proc/sys/net/ipv4/conf` writes `conf/<if>/forwarding`, while a write outside that subtree fails `EROFS`. The kernel was the host's 7.0, not each distribution's | D-10, M-18 |
| K-11 | A table created with `flags owner, persist`: while its owner runs, another process adding a chain or deleting the table gets `EPERM`, and its `flush ruleset` leaves the table in place; after the owner exits the table remains, and a new process claims ownership with the same flags. Probed on 7.0; Debian 13's 6.12 carries the flag in its UAPI header | D-16 |
| K-12 | UAPI headers of each release's kernel: `NFT_TABLE_F_PERSIST` from 6.12 in the set (absent from 6.1 and 6.8); `WGALLOWEDIP_F_REMOVE_ME` in 7.0 only | D-10 |
| K-13 | A NativeAOT binary drives libnftables in-process through `LibraryImport` (1.2 MB, no child process): with libnftables 1.1.3 (Debian 13) and 1.1.6 (Ubuntu 26.04) it creates an owned, persistent table, a second process gets `EPERM` and its `flush ruleset` leaves the table, further writes on the same context succeed, `list` returns JSON, and the table outlives the process. With 1.0.9, the only version Ubuntu 24.04 publishes, `flags owner, persist` is a syntax error | F-01 |
| K-14 | ASP.NET Core under NativeAOT, SDK 10.0.401: gRPC alone (Grpc.AspNetCore 2.84.0) publishes with no trim or AOT warning, as does a Minimal API with source-generated JSON; adding JSON transcoding (10.0.12) raises 39, all inside that package — reflection-based serialisation and `MakeGenericType` — which `TreatWarningsAsErrors` turns into a failed build. A plaintext endpoint set to HTTP/1.1 and HTTP/2 serves HTTP/1.1 only; gRPC works over h2c on a TCP endpoint set to HTTP/2 alone and over a unix socket. Binaries: 11.2 MB gRPC, 9.4 MB Minimal API, 14.3 MB both with transcoding | F-02 |
| K-15 | `RandomNumberGenerator.Fill` makes a NativeAOT process dlopen `libcrypto.so.3` and `libssl.so.3`; with them absent the process aborts. Without that call neither is loaded | F-07 |

Two further facts come from source rather than probe, and are marked so: an absent preshared
key is returned as 32 zero bytes (`wg` prints it as `(none)`), and a genetlink family lookup
for `wireguard` makes the kernel load the module itself.

## 3. Decisions needed

Each lists the options and a recommendation. The recommendation is not a decision.

**D-01 — Forwarding sysctl.** `REQ-FWD-020` sets `forwarding = 1` when `intra_interface = ALLOW`
or `inter_interface != DENY`. It says nothing for `external = ALLOW`, nothing for the case
where the condition is false, and "the corresponding interface" is unclear for `inter`: the
table of SPEC-02 section 5 requires the *destination* interface to forward replies, which
§4.1 promises. Root cause: `REQ-API-075` applies the defaults of SPEC-01 only, and the proto
`Axis` enum has `AXIS_UNSPECIFIED = 0`, so a `forward_policy` naming one axis leaves the others
with no rule.
- (a) `forwarding = 1` on every managed interface; every `DENY` is enforced by nftables alone.
- (b) `1` when the interface's own policy permits any axis, or another managed interface's
  `inter` policy reaches it.
- **Recommend (a)**, with `REQ-API-075` extended to SPEC-02 section 2. The sysctl is a coarse
  gate and nftables is the policy; (b) reproduces the policy in a second place. Under B-04,
  where step 10 is deferred, a `DENY` axis stays unenforced — as it is today.

**D-02 — IPv6 underlay.** `REQ-VAL-020` rejects "any address or CIDR of the IPv6 family",
which literally includes a peer `endpoint`. ADR-0005 argues about the overlay only and never
mentions endpoints.
- **Recommend** IPv6 endpoints accepted, overlay unchanged; hostname resolution may then use
  AAAA records. The alternative keeps the literal reading and says so in `REQ-VAL-020`.

**D-03 — Canonical forms.** The kernel rewrites two values it is given (K-2, K-3), so a spec
holding the original drifts on every pass, and `REQ-VAL-012` misses `10.0.0.5/24` against
`10.0.0.0/24`.
- **Recommend** rejecting `allowed_ips` with host bits under a new reason code — the caller
  may have meant `/32`, so rewriting guesses at intent — and clamping a supplied private key
  before storing it, which changes neither the public key nor any peer's view.

**D-04 — `listen_port = 0`.** SPEC-03 classifies `listen_port` agent-owned, so spec `0`
against a bound port is drift, and rewriting `0` changes the port every pass (K-1).
- (a) Spec `0` is satisfied by any bound port and is never rewritten; the port may change when
  the link is recreated, and documentation says so.
- (b) The first bound port is written into the spec.
- **Recommend (a).** (b) mutates a caller's spec and turns over `revision` without a write.

**D-05 — Route ownership, step 8 of `REQ-RCN-022`.** Nothing says which routes on the device the
agent may remove — an operator's route through `wg0`, the kernel's prefix routes, other tables.
And the common client configuration — address `10.8.0.2/24`, hub `allowed_ips 10.8.0.0/24` —
fails with `EEXIST` on every pass (K-5), leaving a permanent `DEGRADED`.
- (a) The agent marks its routes with a dedicated `rtm_protocol` value and diffs only those; a
  prefix the device already routes counts as present.
- (b) Diff every non-kernel route on the device.
- **Recommend (a).** The kernel holds the mark, so it survives a restart, and no other party's
  route is ever touched.

**D-06 — `0.0.0.0/0` in `allowed_ips` with `manage_routes = true`.** The route collides with, or
displaces, the host default route — including the route to the peer's own endpoint.
- **Recommend** rejecting the combination. Policy routing is outside v1; with
  `manage_routes = false` the entry stays valid and routing is the operator's.

**D-07 — WireGuard interfaces the agent does not manage.** `REQ-FWD-013` covers managed
WireGuard interfaces and `REQ-FWD-015` non-WireGuard ones; traffic towards a foreign WireGuard
interface falls through both. nftables cannot match a link kind in any case.
- **Recommend** defining `external` as every interface outside the managed set.

**D-08 — Client configuration, SPEC-06 section 4.** Four gaps, deferrable until B-05 returns:
`PrivateKey` (BYOK never holds it, a generated key is never stored), `PresharedKey` (the file
needs it, `REQ-RES-022` forbids returning it), the client `Address` (no source named), and the
`Endpoint` port (`node.endpoint` is one `host:port` per node, while interfaces differ). Also
"the interface server address" is singular where `addresses` is a list.
- **Recommend** client private key and preshared key carried in the request, never read from
  the store, with a placeholder line when absent; `node.endpoint` reduced to a host and the port
  taken from the interface.

**D-09 — Configuration keys that contradict requirements.** `defaults:` (forward policy, MTU)
against `REQ-FWD-001` and `REQ-API-075`; `audit.enabled` against the unconditional
`REQ-OBS-020`; `server.socket_mode` against `REQ-SEC-003`. **Recommend** removing all three
keys.

**D-10 — Supported distributions. Decided 2026-09-29: Debian 13 and later, Ubuntu 24.04 LTS and
later.** SPEC-09 section 3 listed Debian 11 and Ubuntu 20.04, whose glibc 2.31 the binary cannot
load (K-9). The floor moved twice the same day — Debian 12 with Ubuntu 24.04, then Debian 13
with Ubuntu 26.04 after K-12, then Ubuntu back to 24.04, whose HWE kernel matches 26.04's.

| | Debian 13 | Ubuntu 24.04, GA kernel | Ubuntu 24.04, HWE kernel |
|---|---|---|---|
| Kernel | 6.12 | 6.8 | 7.0 |
| glibc | 2.41 | 2.39 | 2.39 |
| systemd | 257 | 255 | 255 |
| `iptables` backend | nft | nft | nft |
| Owned table that persists, `NFT_TABLE_F_PERSIST` | yes | **no** | yes |
| `WGALLOWEDIP_F_REMOVE_ME` | no | no | yes |
| On the .NET 10 supported-OS list | yes | yes | yes |

What the floor buys:
- WireGuard in tree on every supported kernel: the `wireguard-dkms` path and `KERNEL_TOO_OLD` in
  check 1 of `REQ-API-050` have no case left.
- The `REQ-CFG-011` combination works on systemd 255 and 257 (K-10): the fallback of
  `REQ-CFG-012` has no case left, and `REQ-CFG-013` covers two versions rather than six.
- One `linux-x64` binary and one `.deb`, depending on `libc6 (>= 2.34)` as measured in K-9, on
  releases the .NET 10 support statement covers.
- iptables-legacy is an operator's explicit choice rather than a distribution default, which
  lowers the frequency of M-17 without removing it.

What it leaves open: the GA kernel of Ubuntu 24.04 lacks the flag D-16 rests on, so D-16 now
carries a kernel condition. No M item goes away on any kernel of the set — K-1 to K-7 were
observed on 7.0. `WGPEER_F_REPLACE_ALLOWEDIPS` stays the one mechanism for changing a peer's
allowed IPs, since Debian 13 lacks `WGALLOWEDIP_F_REMOVE_ME`.

"And later" means Debian stable releases and Ubuntu LTS releases. Interim Ubuntu releases are
outside the tested matrix.

**D-11 — Token reload, `REQ-CLI-016`.** "Signal the running agent" has no available mechanism:
no pid file (the lock rationale of SPEC-03 rejects them) and no child process
(`REQ-SEC-041` — the CLI is the product binary).
- (a) The agent watches the token file's directory and reloads on change; `SIGHUP` stays for
  operators, and `REQ-CLI-016` is struck.
- (b) A POSIX record lock, whose holder pid `F_GETLK` returns, then `kill`.
- **Recommend (a).** It also covers an operator editing the file by hand.

**D-12 — A link with no private key at adoption.** `REQ-RCN-061` stores "the existing key";
there is none, and `REQ-RES-012` generates only at creation. **Recommend** generating one with
a `WARN` finding — no peer can be connected to a keyless device, so nothing is disturbed —
over refusing with a `FAIL`.

**D-13 — Failure inside a pass.** `REQ-RCN-040` marks `DEGRADED` but not whether the remaining
steps run. **Recommend** skipping only the steps that depend on the failed one, and always
attempting step 10 while the link exists: an address that fails to apply is no reason to leave
policy unenforced. Other interfaces are unaffected.

**D-14 — Audit scope, `REQ-OBS-020`.** "Every state-changing operation": corrections reconcile
makes, CLI writes while the agent is stopped, failed attempts. **Recommend** API and CLI writes,
failed ones included; reconcile corrections go to the log and the drift metric, not the audit.

**D-15 — Carry-overs from the removed implementation.** `packaging/systemd/` was written with
the Go implementation (800460b) and restates the unit of SPEC-09 section 4. **Recommend**
removing it and deriving it again under B-03, as `src/` was.

**D-16 — Kernel-enforced ownership of `table inet wg_agent`.** Without it, any process can
change or delete the table, and `flush ruleset` — the first rule line of the stock
`/etc/nftables.conf` on both supported distributions — removes every `DENY` rule until the next
periodic pass of `REQ-RCN-020`. With the owner and persist flags (K-11) the kernel refuses
other writers while the agent runs, `flush ruleset` passes the table by, the table outlives the
agent as `REQ-API-074` requires, and the next start reclaims it.
- Kernel condition: 6.9 or later. Debian 13 meets it; Ubuntu 24.04 meets it with the HWE kernel
  (`linux-generic-hwe-24.04` or `linux-virtual-hwe-24.04`, 7.0) and not with the GA kernel (6.8).
  Requiring the HWE kernel keeps D-16. Accepting the GA kernel leaves either the window up to the
  next periodic pass, or the event-driven repair below — on every node, since two paths would
  double the test surface.
- **Recommend** adopting it with the HWE kernel required on Ubuntu: the table is created owned
  and persistent, startup fails with a named reason when the kernel refuses the flags, and documentation states that the table cannot
  be edited by hand while the agent runs. An architectural choice, so an ADR comes with the
  SPEC-02 change.
- The alternative, for the ADR: subscribe to nftables events, tell the agent's own changes from
  others' by the generation's process id, and resynchronise on a foreign one or a lost event.
  It detects instead of preventing: a window stays between the foreign write and the repair, and
  a connection opened in that window outlives it through the `established` accept of
  `REQ-FWD-011` wherever conntrack is still tracking.
- Cost of the flags: every write to the table goes through one netlink socket held for the
  agent's lifetime, since ownership belongs to that socket; the table is reclaimed at startup.

## 4. Defects with an evident fix

### 4.1. Language residue and stale references

- **M-01** Go: `REQ-API-078` "the Go version"; `REQ-CFG-020` "statically with `CGO_ENABLED=0`
  for `amd64` and `arm64`" and `REQ-CFG-036` `arm64`, against ADR-0012 (linux-x64, glibc,
  dynamic); the `go_version` label of `wg_agent_build_info`; the `version` row of SPEC-12;
  `REQ-SEC-050` "whose `String()` returns" — every textual and serialised form is meant;
  `state.path: state.db`, a database-engine name.
- **M-02** SPEC-10 cites ADR-0002, superseded by ADR-0012. The rationale of `REQ-API-078` claims
  the "five values" of the `agent` row of `REQ-DIA-020`, which lists four.
- **M-03** False rationale: `REQ-RES-025` — 180 s is not three times 120 s; it is
  `REJECT_AFTER_TIME`, and an idle peer without keepalive reads offline while healthy. SPEC-04
  section 9 — an advisory lock is released when its holder exits, as SPEC-03 itself says.
  `REQ-RCN-023` — "desired state holds no endpoint" holds for adopted peers only.
  `REQ-CLI-006` — adoption runs with the agent stopped, so there is no API response to render.
  SPEC-11 section 6 — reconcile never reads other tables or `/proc/net/udp`.
- **M-04** Schedule inside a spec: SPEC-11 section 6 "M1 rather than M4", SPEC-12 "a milestone
  later", "the MVP".

### 4.2. Contradictions

- **M-05** Store lock: the rationale of `REQ-RCN-006` places an advisory lock on the store file,
  which SPEC-03 section 2 replaces by rename — a later opener locks the new inode, so two
  processes hold "the" lock. Lock a file that is never renamed; state that a store transaction
  is a synced temporary file, a rename and a synced directory.
- **M-06** `REQ-API-010` "every peer in one call" against `REQ-API-011` and `REQ-API-012`, which
  page `ListPeerStatus` at 100. Reword to "without a call per peer".
- **M-07** `REQ-KEY-021` applies `REQ-KEY-012` to a generated preshared key, forbidding the store
  that must hold it. Cite `REQ-KEY-011` and `REQ-KEY-013` only.
- **M-08** `REQ-DIA-010` must list base chains of other tables; `REQ-FWD-040` forbids reading
  them. Narrow `REQ-FWD-040` to modify and delete.
- **M-09** `REQ-CLI-022` forbids a token value in human-readable output; `REQ-CLI-011` requires
  `token add` to print the value it generated. Except that value.
- **M-10** `REQ-FWD-022` excepts writes "required by `REQ-FWD-023`", a rejection rule that
  requires none, and no requirement says what `nat.enable_uplink_forwarding` does. Add the
  uplink write it names, and point the exception at it.
- **M-11** `REQ-RCN-010` requires every spec field classified; its own table omits
  `manage_routes` and both `labels`.
- **M-12** `REQ-API-034` promises all-or-nothing through "one device configuration call". The
  kernel applies a set message peer by peer without rollback, and a large set spans messages.
  Only the store side can be atomic; the kernel side converges through reconcile.

### 4.3. Missing rules the kernel forces

- **M-13** A peer whose public key is the interface's own (K-4): error rule, else reconcile
  re-adds it forever without ever reporting failure.
- **M-14** MTU (K-6): an error bound — at least 68, at most 65535 — beside the warning of
  `REQ-VAL-032`; and step 6 before step 5 in `REQ-RCN-022`, since a sub-68 MTU makes every
  address write fail.
- **M-15** `REQ-RES-011` and `REQ-VAL-010` exclude `all` and `default` (K-7).
- **M-16** Ranges: `listen_port` and `persistent_keepalive` are 16-bit in the kernel and 32-bit
  in the proto. Format: `private_key` and `preshared_key` as base64 of 32 bytes (`REQ-VAL-011`
  covers public keys only); `endpoint` as host and port.
- **M-17** Comparisons reconcile needs: an all-zero preshared key equals an absent one;
  interface addresses compare by address and prefix length, host bits kept; the kernel prefix
  route of an address is never removed.
- **M-18** Netlink framing: one peer's allowed IPs may arrive across several dump messages, and a
  large set is split across messages with `REPLACE_ALLOWEDIPS` on the first fragment only.
- **M-19** Hostname endpoints (`REQ-VAL-033`): when resolution happens, which families, and the
  condition on failure.
- **M-20** iptables-legacy chains are invisible to nftables: `foreign_forward_chains` reports
  `UNKNOWN` while legacy tables are loaded, rather than `PASS`.
- **M-21** Rationale lines: `CAP_NET_ADMIN` alone may write `net.*` sysctls owned by root
  (`REQ-SEC-030`); startup check 1 resolves the genetlink family, which loads the module without
  a child process; the criterion for `KERNEL_TOO_OLD`.

### 4.4. Gaps and ambiguities

- **M-22** Authorization: a `reader` calling a write has no reason code, though `REQ-API-041`
  requires one; `REQ-SEC-021` is unreachable, since sockets grant `admin` and every token has a
  role. The same gap for a request refused under `REQ-KEY-015`, and for `CreatePeer` on an
  interface desired state does not describe. The role table omits `GetVersion`,
  `GenerateKeyPair` and `GenerateClientConfig`.
- **M-23** No reason code maps to a gRPC or HTTP status; section 7 gives example cases per
  status. A per-code table is contract, frozen by `REQ-API-061`, and is drafted for review with
  the fix.
- **M-24** Drift (`REQ-OBS-004`) excludes a spec being applied for the first time and a link
  reconcile has just created, or every write counts as drift.
- **M-25** Release (`REQ-FWD-016`, `REQ-RCN-073`): the interface's nftables rules go, its
  routes and addresses stay. `REQ-RCN-073` carries two actions and is split.
- **M-26** Node-wide state: `REQ-RCN-042` serialises per interface; `table inet wg_agent` spans
  interfaces and needs its own serialisation.
- **M-27** Files a CLI writes under `sudo` — the store and the token file — take the owner of
  their directory, which packaging creates for the agent account; otherwise `REQ-RCN-004` and
  `REQ-SEC-082` fail and the agent is locked out.
- **M-28** Status fields are defined by the tables of SPEC-01 sections 3.3 and 4.3 alone; one
  requirement binds them, as `REQ-API-075` binds the defaults. `last_reconcile_at` is the last
  completed attempt.
- **M-29** Smaller points: `REQ-API-077` — any peer write turns over the interface revision, not
  only membership; `REQ-API-022` — a timeout answers like `REQ-API-021`; `REQ-API-051` — the
  first pass counts whatever its outcome; `REQ-API-021` — 202 is `OK` over gRPC; startup
  check 3 reports an unwritable store as `STORE_CORRUPT`; `REQ-SEC-081` — an invalid file on
  reload keeps the previous tokens; `REQ-CFG-003` — labels are unique; `REQ-CFG-042` — flag
  names follow a rule, as `REQ-CFG-041` does for variables; `REQ-VAL-013` exempts port 0;
  `REQ-VAL-030` — overlap across the peers of one interface; `REQ-VAL-031` — every subnet of the
  interface; `REQ-VAL-022` — a referenced interface deleted later; `REQ-KEY-003` — "atomically"
  against what; `REQ-KEY-039` — the QR format; `REQ-DIA-049` — which `.wants` directories; the
  wg-quick configuration path, and `/etc/wireguard` being unreadable by the agent account, so
  `REQ-DIA-046` answers differently over the API than under `sudo doctor`.
- **M-30** Per-peer counters reset when a peer is removed and re-added, while the interface
  `instance_id` of `REQ-RES-026` stays — through a delete and create of one key, or
  `replace_all`. State the limit.

### 4.5. Duplicates

One fact, two homes. Strike one of each pair and repoint its citations, choosing by the module
boundary table: `REQ-KEY-001` and `REQ-RES-012`; `REQ-KEY-002` and `REQ-RES-013`; `REQ-KEY-020`
and `REQ-RES-022`; `REQ-VAL-021` and `REQ-FWD-023`; the name pattern in `REQ-VAL-010` and
`REQ-RES-011`; `REQ-LIF-011` and `REQ-RCN-005`; `REQ-LIF-030` and `REQ-CFG-021`.

### 4.6. SPEC-10, a Draft module

SPEC-10 is `Draft`, yet SPEC-03 section 9 takes its scale targets from `REQ-LIF-040` and SPEC-12
cites `REQ-LIF-020` and `REQ-LIF-051`. Before it is accepted: import names the validation it
runs against the target host — without `REQ-VAL-015` a name held by a foreign link there is
taken over silently — and does not carry adoption records, whose sysctl values belong to the
source node; `REQ-LIF-023` says whether the override replaces or merges; `REQ-LIF-031` names its
limit, how the absence of logrotate is detected, and what happens at the limit without breaking
`REQ-OBS-020`; the audit file is reopened after rotation.

## 5. Planning and documentation

- **P-01** Backlog. B-04 keeps `REQ-FWD-020` and defers `REQ-FWD-001`, the default it depends
  on; it defers `REQ-FWD-021` and `REQ-FWD-040`, prohibitions that cost nothing to meet; it
  describes a reconcile-time `SYSCTL_WRITE_DENIED` no requirement defines. `REQ-API-072` is kept
  while `REQ-API-031`, which it invokes, is deferred. Go residue at lines 87, 99, 139 and 151.
  Implementation at zero makes every "implemented" claim in the backlog void; revise it after
  the spec changes land.
- **P-02** `50-guides/running-tests.md` claims checks no code performs (the store's own tests,
  the reboot round-trip "checked on every change", the behaviour of `serve` under B-04); its
  adoption example omits `nat`, which `REQ-RCN-066` rejects, and its flags are specified
  nowhere; it says Docker cannot serve `REQ-CFG-013`, where the rationale in SPEC-09 says a
  container is a sufficient gate. The spec wins; the guide is corrected.
- **P-03** Tooling risks, for [risks](risks.md) once the tooling step starts: YAML under
  NativeAOT needs a source-generated parser (`REQ-CFG-037`); the REST mapping needs gRPC JSON
  transcoding under NativeAOT, to be spiked before B-05.

## 6. Foundations

Stack and scope choices below the level of a single requirement, reviewed once the platform
floor was fixed. **F** items are decisions; each that is adopted lands as its own ADR, because
ADR-0012 already bundles three decisions and an accepted ADR cannot be edited.

**F-01 — nftables through libnftables, and the Ubuntu release it needs.** ADR-0012 leaves the
nftables mechanism open ("an nftables library rather than invoking `nft`", deferred under B-04).
Two routes meet `REQ-SEC-041`:
- (a) Encode nfnetlink batches in managed code. No dependency; the largest single block of code
  in the kernel edge — tables, chains, expressions, verdicts, batches, and a parser for listing.
- (b) Call libnftables, the netfilter project's own library behind `nft`, in-process (K-13).
  Rules are written in nft syntax, or its JSON form so no string is ever assembled from input;
  listing returns JSON, which `REQ-DIA-010` and the `nft_rules` check read directly; one context
  held for the agent's lifetime is the owning socket D-16 needs. Cost: a package dependency
  (`libnftables1`, which pulls libnftnl, libmnl and jansson), a native surface ADR-0012 says it
  does not have, and one context serialised across callers — which M-23 requires anyway.

(b) works on Debian 13 and on Ubuntu 26.04. It does not work on Ubuntu 24.04: its only
libnftables, 1.0.9, cannot express the persist flag, so D-16 there would need (a) regardless.
**Recommend (b) with the Ubuntu floor at 26.04 LTS** — stock kernels then carry every flag, the
HWE step of D-16 disappears, and so does (a). Keeping Ubuntu 24.04 means (a) plus the HWE kernel.

**F-02 — One API protocol. Decided 2026-09-29 against the recommendation: REST, contract in
OpenAPI — ADR-0014.** SPEC-04 carries two surfaces: gRPC, and REST through
`google.api.http` annotations (`REQ-API-002`, `REQ-API-063`, `REQ-API-081`, `REQ-API-033`). K-14
settles the cost of the second: the only first-party REST route from a `.proto` does not build
under this project's warning policy, and it cannot share a plaintext endpoint with gRPC.
- (a) Both, with transcoding's warnings suppressed and two TCP endpoints.
- (b) gRPC only. The `.proto` stays the single contract of ADR-0003, clients are generated for
  every language the product names (Go for Terraform and Kubernetes operators), and one
  protocol runs over the unix socket. Operators script through the CLI, or `grpcurl` against the
  `.proto` files shipped in the package.
- (c) REST only, through Minimal APIs. The smallest binary and `curl` without tools, but the
  `.proto` stops being the contract and ADR-0003 is replaced rather than trimmed.
- **Recommend (b).** The consumers in product.md are machines. `REQ-API-002`, `REQ-API-063`,
  `REQ-API-081`, `REQ-API-033` and `REQ-RES-021` fall away, the error model of M-20 needs gRPC
  codes only, and `WatchPeerStatus` returns later as a stream without a second design. With
  F-04, the whole surface is one gRPC service on one unix socket.

**F-03 — Configuration format.** SPEC-09 specifies a YAML file (`REQ-CFG-037`), and NativeAOT
has no first-party YAML reader. Once D-09 removes the `defaults:` block every key is a scalar,
about eighteen of them, already mirrored one-to-one by `WG_AGENT_<PATH>` variables
(`REQ-CFG-001`).
- (a) YAML with a third-party source-generated reader.
- (b) JSON through the first-party configuration binder, which is source-generated for AOT.
- (c) One `KEY=VALUE` file — `/etc/default/wg-agent`, which `REQ-CFG-004` already loads — read by
  the agent and the CLI alike, then environment, then flags.
- **Recommend (c).** One file, one syntax an operator of a Debian service expects, no parser
  dependency, and one layer of precedence fewer. The token file, written only by the CLI, becomes
  JSON through the source-generated serializer.

**F-04 — The loopback HTTP listener and its tokens. Decided 2026-09-29: one secret on a
network listener, plain HTTP on a private network — ADR-0015.** Both listeners are local (ADR-0009), and
the unix socket already identifies its caller through the kernel. The token system exists to give
local callers distinct roles: generation, storage at `0600`, constant-time comparison, reload,
revocation, four `token` subcommands, and the install script printing a secret.
- (a) Keep it.
- (b) Unix socket only; the role follows the caller's group from peer credentials — members of
  one group are `admin`, of another `reader`. A remote platform keeps its own hop: SSH forwards a
  unix socket, and a reverse proxy can target one.
- **Recommend (b)** for v1. No bearer secret exists to leak, and `REQ-SEC-071` to `REQ-SEC-082`,
  `REQ-CLI-010` to `REQ-CLI-016` and `REQ-CFG-031` to `REQ-CFG-033` either fall away or shrink to a
  group mapping. The metrics listener is separate and unaffected. Reversible: a token listener
  can be added later without changing the socket's contract.

**F-05 — QR codes, `REQ-KEY-039`.** A QR encoder is a dependency, and any platform renders one
from the `.conf` text in a line of code. **Recommend** striking it.

**F-06 — Decision records.** When F-01 to F-05, F-07 and D-10 settle, each gets an ADR of its own; the
same ADRs supersede the stale parts of accepted ones — the Go tooling of ADR-0003, the transport
rule of ADR-0004 that `REQ-KEY-014`'s removal left behind, the `arm64` of ADR-0010 — rather than
leaving them contradicting the spec. `go_version` also sits in `service.proto` and is renamed
before the contract freezes.

**F-07 — Source of randomness.** Key generation (`REQ-KEY-001`) through `RandomNumberGenerator`
brings OpenSSL in at run time (K-15). `getrandom(2)`, called through libc like the netlink
sockets, gives the kernel's CSPRNG with no library; BouncyCastle then only derives public keys
and needs no generator. **Recommend** `getrandom(2)` — the dependency set stays libc, plus
libnftables under F-01.

## 7. Order of work

Superseded by the phases of the [roadmap](roadmap.md) once the specification was cut to the
wrapper; section 8 records where each item landed.

1. Decisions D-01 to D-16 and F-01 to F-07.
2. One spec change per module, each with its version bump, carrying the decided items and the
   M items of that module.
3. P-01 and P-02 against the amended spec.
4. `docs/check-*.sh` passing, then the tooling step of the roadmap.

## 8. After the wrapper cut

Reassessed on 2026-09-30, after ADR-0013 to ADR-0015 and the cut of the specification to the
wrapper. Every item above now stands in one of five states.

| State | Items |
|---|---|
| **Resolved by the cut** | D-03, D-04, D-05, D-06 (`REQ-VAL-044`), D-08, D-09, D-11, D-15; F-07; M-01 except the deferred SPEC-08 label, M-03, M-05, M-06, M-07, M-09, M-13, M-14, M-15, M-16, M-23, M-26, M-27; P-01, P-02, P-03 |
| **Decided** | D-02 — IPv6 endpoints stay refused with the overlay; D-10 — Debian 13 and Ubuntu 24.04 onwards; F-02 — REST, ADR-0014; F-03 — one `KEY=VALUE` file, R-03; F-04 — one token, ADR-0015; F-05 — QR deferred to B-11 rather than struck |
| **Waits with its backlog entry** | D-01, D-07, D-16, F-01, M-10 with B-04; D-12, M-25 with B-10; the reconcile half of D-13, M-11, M-17 with B-09; D-14, M-24 with B-01; M-02 with B-02; M-08, M-20 and the SPEC-11 half of M-04 with B-06; M-12 with B-05; M-22 with B-08; M-30 with B-12 |
| **No longer applies** | M-18 — no netlink; M-19 — `wg` resolves a hostname when it applies the file, and `REQ-VAL-033` warns; M-21 — no sysctl, no generic netlink, no module check |
| **Open, small, for P1 to P3** | M-28 — status fields defined only by tables; the rest of M-29 — an unwritable store reported as `STORE_CORRUPT`, the flag naming rule of `REQ-CFG-042`, the scope of `REQ-VAL-030`; F-06 — R-13 |

The apply half of D-13 is `REQ-APL-008`: a failed change is restored and reported rather than
left degraded.

### Review points

Decisions taken during the cut, for the operator to confirm or overturn; each is a small edit
either way.

- **R-01** SPEC-13, the new module holding the wrapper's mechanics, is `Review`. P1 implements
  it once accepted.
- **R-02** `listen_port` defaults to 51820 and `0` is refused (`REQ-VAL-036`): a port the kernel
  picks changes at every restart and breaks every client configuration naming it.
- **R-03** The configuration is one `KEY=VALUE` file, `/etc/default/wg-agent`, rather than YAML:
  seven scalar keys, no parser dependency.
- **R-04** An `allowed_ips` entry with host bits is refused (`REQ-VAL-041`) rather than
  normalised.
- **R-05** A default route in `allowed_ips` is refused (`REQ-VAL-044`), so a full tunnel through a
  peer is not possible in v1.
- **R-06** Forwarding and NAT stay the host's through P3 ([SPEC-13](../20-spec/SPEC-13-applying-changes.md)
  section 5). An interface the agent creates carries no `PostUp`, so a node whose peers must reach
  each other or the networks beyond needs `ip_forward` and a masquerade rule set once on the host.
  The P3 guide shows how; pulling a minimal NAT switch forward from B-04 is the alternative.
- **R-07** A client configuration exists only in the response that created the peer with a
  generated key pair (`REQ-KEY-042`). A peer created with a supplied public key has none, and no
  later operation rebuilds one until B-11.
- **R-08** The package generates the token silently at installation (`REQ-CFG-045`); the operator
  reads it as root, or runs `token rotate`, which prints a new one.
- **R-09** The CLI acts directly, without the daemon (`REQ-CLI-002`), so P1 delivers a usable CLI
  before any API exists.
- **R-10** `RotateInterfaceKey`, `GenerateKeyPair` and QR codes wait in B-11; meanwhile
  `UpdateInterface` with a new `private_key` rotates a key.

Open questions that change the plan if answered differently:

- **R-11** A hand edit to a file the agent created is replaced at its next write. Should the agent
  detect the edit and refuse instead — the open question of SPEC-13?
- **R-12** `docs/check-traceability.sh` treats every requirement of an `Implemented` module as
  needing a test, the deferred ones included. It has to read the backlog before the first module
  of P1 is marked `Implemented` — a tooling task at the start of P1.
- **R-13** The build stack — .NET 10, NativeAOT, `linux-x64` — lost its ADR when ADR-0012 was
  superseded; `REQ-CFG-044` and `CLAUDE.md` carry it. A short ADR restating it would keep the fact
  in one home.

### Answers, 2026-09-30

R-01 accepted — SPEC-13 is `Accepted`. R-02 confirmed: every interface file carries `ListenPort`.
R-03, R-04, R-05, R-08, R-09 and R-10 confirmed as proposed. R-07 confirmed: the operator or the
orchestrator keeps the client configuration, and a lost one is replaced by recreating the peer.
R-11: a hand edit is overwritten, and SPEC-13 says so. R-12 is a tooling task at the start of P1.
R-13: [ADR-0016](../10-decisions/ADR-0016-dotnet-nativeaot-build.md). R-06 stays open, below.

A second pass over the cut added three requirements a P1 implementer would otherwise have had to
invent: `REQ-RES-036` — null rather than zero when an interface has no device; `REQ-APL-009` —
interface files owned by root at `0600`, replaced atomically; and `REQ-APL-006` now names the
addition and removal of a site-to-site peer, not only its change. `ListInterfaces` is stated to
return only the interfaces the agent created.

### Second round

- **R-06 — NAT and forwarding, open.** The operator asked why the agent cannot write `PostUp` and
  `PostDown` itself, as is done by hand. It can; what it must not do is take the lines from a
  caller. A hook runs as root whenever `wg-quick` brings the interface up, and the token crosses
  the private network in clear, so a free-form hook field would hand root to whoever reads that
  network — the reason of ADR-0007.
  - (a) The host sets `ip_forward` and a masquerade rule once, for every interface. The plan is
    unchanged.
  - (b) Structured NAT rendered as hooks. An interface carries `nat.enabled` and
    `nat.masquerade_out_interface`; the agent itself writes fixed `PostUp` and `PostDown` lines
    from them — `sysctl` for forwarding, an `nft` table of the interface's own holding one
    masquerade rule, dropped at `PostDown`. Only validated names and subnets reach the lines. It
    costs a new ADR restating ADR-0013 with this exception, `REQ-APL-003` widened by two keys, the
    slice `REQ-FWD-030` and `REQ-FWD-031` brought forward from B-04, and a dependency on
    `nftables`. A host firewall whose forward policy drops traffic — ufw's default — still has to
    allow it, under ADR-0008.
  - (c) Free-form hooks from the API. Refused: remote root.
  - (d) Free-form hooks from a root-owned file on the node, copied into every file the agent
    renders. As flexible as manual work and safe, since only root edits the file; the agent then
    renders content it did not validate.
- **R-14 — Keepalive in the client configuration.** The file carries no `PersistentKeepalive`.
  A client behind NAT that sends nothing for a while loses its mapping, and the node cannot reach
  it until it sends again. An optional request field would set it.
- **R-15 — Choosing a peer's address.** The caller chooses each peer's allowed IPs; IPAM belongs to
  the platform under product.md, and by hand it is one of the chores. The agent could assign the
  lowest free `/32` of the interface's first subnet when a peer is created with a generated key
  pair and no `allowed_ips` — a narrow exception, moving one line of product.md.
- **R-16 — Moving the interfaces configured by hand.** The agent manages only what it creates,
  and adoption waits in B-10. An interface can still move without re-keying a client: disable its
  `wg-quick@` unit, move its file out of `/etc/wireguard/`, create the interface through the agent
  with the same private key, addresses and port, and add each peer with its public and preshared
  keys. Clients see the same key, address and port. The P3 guide can document it; a CLI helper
  reading the moved file is the next step up; adoption the one after.

### Answers, second round, 2026-09-30

- **R-06** — hooks through the CLI alone, alternative D of
  [ADR-0017](../10-decisions/ADR-0017-operator-hooks-through-the-cli.md), which carries ADR-0013
  forward. `post_up` and `post_down` are interface fields the CLI sets (`REQ-CLI-024`); the API
  neither returns nor changes them (`REQ-API-084`, `REQ-API-085`); a line break is refused
  (`REQ-VAL-045`); `REQ-APL-003` admits the two keys.
- **R-14** — the client configuration carries `PersistentKeepalive`, 25 unless the request says
  otherwise (`REQ-KEY-046`).
- **R-15** — a generated peer asked for no address gets the lowest free host address of the
  interface's first subnet (`REQ-KEY-047`, `REQ-VAL-046`).
- **R-16** — `interface create` reads a JSON document holding the interface and its peers, and
  creates them in one write (`REQ-CLI-025`); the operator fills it once from each hand-kept file.
  No further migration is wanted: by the operator's statement it is an internal tool.

### Third round

- **R-17 — Trimming for an internal tool.** Several parts of the plan serve parties other than the
  operator: the positioning of product.md towards Terraform providers, Kubernetes operators and
  VPN platforms; the install script and release pipeline of B-03; published clients and the CI
  compatibility gate of B-05; roles and several principals of B-08; the contract test of P2. Each
  could be marked not planned rather than deferred, which removes it from every future count;
  the contract test is the one worth keeping, since the operator's own orchestrator calls the API.

### Answer, third round, 2026-09-30

- **R-17** — trimmed in full. Struck rather than deferred: the install script and release
  pipeline, `REQ-CFG-029` to `REQ-CFG-036`, with
  [ADR-0018](../10-decisions/ADR-0018-deb-installed-by-the-operator.md) superseding ADR-0010; the
  compatibility gate in CI, `REQ-API-061`; roles and several principals, `REQ-SEC-020`,
  `REQ-SEC-021`, `REQ-SEC-075` and `REQ-SEC-079`. product.md positions the agent as the internal
  tool it is. TLS stays deferred, and the contract test of P2 stays, since the operator's own
  orchestrator calls the API. The plan holds 317 live requirements, 162 of them in P1 to P3.
