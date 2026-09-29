---
updated: 2026-09-29
---

# Specification audit, September 2026

A read of all twelve modules before the implementation is rebuilt from them — next action 1 of
the [roadmap](roadmap.md). Nothing here is normative. Each item either becomes a spec change
through the workflow in `CLAUDE.md`, or is closed with a reason recorded against it.

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

**D-10 — Supported distributions. Decided 2026-09-29: Debian 13 and later, Ubuntu 26.04 LTS and
later.** SPEC-09 section 3 listed Debian 11 and Ubuntu 20.04, whose glibc 2.31 the binary cannot
load (K-9). A floor of Debian 12 and Ubuntu 24.04 was set first and raised the same day, once
K-12 showed what the older pair costs.

| | Debian 12 | Ubuntu 24.04 | **Debian 13** | **Ubuntu 26.04** |
|---|---|---|---|---|
| Kernel | 6.1 | 6.8 | 6.12 | 7.0 |
| glibc | 2.36 | 2.39 | 2.41 | 2.43 |
| systemd | 252 | 255 | 257 | 259 |
| `iptables` backend | nft | nft | nft | nft |
| Owned table that persists, `NFT_TABLE_F_PERSIST` | no | no | yes | yes |
| `WGALLOWEDIP_F_REMOVE_ME` | no | no | no | yes |
| On the .NET 10 supported-OS list | no | yes | yes | yes |

What the floor buys:
- Kernel-enforced ownership of `table inet wg_agent` (K-11), which neither older release
  carries — D-16.
- WireGuard in tree on every supported kernel: the `wireguard-dkms` path and `KERNEL_TOO_OLD` in
  check 1 of `REQ-API-050` have no case left.
- The `REQ-CFG-011` combination works on systemd 257 and 259 (K-10): the fallback of
  `REQ-CFG-012` has no case left, and `REQ-CFG-013` covers two versions rather than six.
- One `linux-x64` binary and one `.deb`, depending on `libc6 (>= 2.34)` as measured in K-9, on
  releases the .NET 10 support statement covers.
- iptables-legacy is an operator's explicit choice rather than a distribution default, which
  lowers the frequency of M-17 without removing it.

What it does not buy: every behaviour in K-1 to K-7 was observed on 7.0, the newest kernel of
the set, so no M item goes away. `WGALLOWEDIP_F_REMOVE_ME` is absent from Debian 13, so
`WGPEER_F_REPLACE_ALLOWEDIPS` stays the one mechanism for changing a peer's allowed IPs.

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
- **Recommend** adopting it: the table is created owned and persistent, startup fails with a
  named reason when the kernel refuses the flags, and documentation states that the table cannot
  be edited by hand while the agent runs. An architectural choice, so an ADR comes with the
  SPEC-02 change.

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

## 6. Order of work

1. Decisions D-01 to D-16.
2. One spec change per module, each with its version bump, carrying the decided items and the
   M items of that module.
3. P-01 and P-02 against the amended spec.
4. `docs/check-*.sh` passing, then the tooling step of the roadmap.
