/**
 * Composition root — wires config → ipc client → matter bridge and runs.
 *
 * Placeholder until story S1-5. The module layout this file will compose is
 * fixed by docs/BLUEPRINT.md §2.1; do not add logic here that belongs in
 * matter/, mapping/, or ipc/.
 */
// process.stderr.write, not console.error: keeps the `no-console` lint rule
// on uniformly (no override) until pino lands in S1-5.
process.stderr.write(
  "windows-google-home-matter: not implemented yet (see BACKLOG.md — Sprint 0/1)\n",
);
process.exit(1);
