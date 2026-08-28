# ADR-010: Windows mDNS scope-id workaround

- **Status**: accepted (shipped in MatterHelm 0.4.2)
- **Date**: 2026-08-25
- **Story**: S10-9

## Context

MatterHelm announces its commissionable Matter service on Windows, but Google
Home reports that it cannot find the device during pairing. Packet-level
instrumentation showed the bridge receiving Nest Hub `_matterc` queries while
sending zero responses.

In matter.js 0.17.7, `NodeJsUdpSocket` labels inbound Windows packets with the
interface's numeric IPv6 scope id (for example `"5"`). `MdnsServer` passes that
identifier to `NodeJsNetwork.getIpMac`, which indexes `os.networkInterfaces()`
by friendly name (for example `"Wi-Fi"`). The lookup returns no addresses, the
empty record set is cached, and the query is silently dropped. Creating the
response channel can also pass the numeric id to static helpers that expect a
friendly name. The defect remains in the inspected 0.17.8 and 0.17.9 releases;
the current upstream project is [matter-js/matter.js](https://github.com/matter-js/matter.js).
[Issue #2281](https://github.com/matter-js/matter.js/issues/2281) and
[PR #2322](https://github.com/matter-js/matter.js/pull/2322) are related but do
not fix this identifier mismatch.

## Decision

Install a Windows-only, process-global compatibility patch in
`bridge/src/matter/adapter.ts` immediately before `ServerNode.create`. Resolve
numeric identifiers against IPv6 `scopeid` entries in `os.networkInterfaces()`
and then delegate to the original matter.js methods. Wrap only the three
lookups on the broken path:

- `NodeJsNetwork.prototype.getIpMac`, used to build per-interface mDNS records;
- `NodeJsNetwork.getNetInterfaceZoneIpv6`, used when creating an IPv6 response
  channel; and
- `NodeJsNetwork.getMulticastInterfaceIpv4`, used when creating an IPv4
  response channel.

Do not patch `getNetInterfaceForIp`. It accepts an IP address rather than an
interface identifier, and its numeric Windows return value is intentionally
used as an IPv6 socket zone. The resolver itself is platform-agnostic pure
logic; only installation is gated to `win32`. Installation is idempotent.

This keeps all matter.js imports and version-specific behavior at the adapter
boundary required by `CLAUDE.md` and the blueprint.

## Consequences

- Incoming mDNS queries can build records and create response sockets on both
  pinned and automatically selected Windows interfaces, allowing Google Home
  to discover the commissionable bridge.
- The workaround mutates matter.js methods process-wide. MatterHelm already
  supports one `MatterNode` per sidecar process, and the wrappers preserve the
  original implementations rather than copying upstream logic.
- Non-Windows behavior is unchanged. Friendly interface names and unresolvable
  identifiers retain upstream behavior.
- Remove the patch and this ADR after upgrading to a matter.js release whose
  Windows inbound identifier is accepted consistently by mDNS record lookup
  and response-channel creation. Versions checked: 0.17.7 through 0.17.9.
