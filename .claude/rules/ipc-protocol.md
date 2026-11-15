---
paths:
  - "bridge/src/ipc/protocol.ts"
  - "app/MatterHelm/Sidecar/Protocol.cs"
  - "docs/BLUEPRINT.md"
---

# IPC protocol parity

For every protocol change:

1. Update `bridge/src/ipc/protocol.ts` and `app/MatterHelm/Sidecar/Protocol.cs` together.
2. Update the normative frame definitions in BLUEPRINT section 2.3.
3. Preserve the current version and handshake for compatible additions. For a breaking change,
   bump `protocol` in `hello` and add an ADR explaining compatibility and rollout.
4. Add or update bridge schema tests and app protocol tests, including invalid and boundary data.
5. Update `app/MatterHelm/DemoAssets/StubSidecar.js` and the
   `app/MatterHelm/Demos/WiredDemo.cs` fixtures so acceptance evidence uses the real contract.
