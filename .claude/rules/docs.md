---
paths:
  - "docs/**"
  - "README.md"
  - "CHANGELOG.md"
  - "BACKLOG.md"
  - "CONTRIBUTING.md"
  - "SECURITY.md"
  - "SUPPORT.md"
---

# Documentation rules

- Say "maintainer", not "owner", for project responsibility.
- Remove personal identifiers such as usernames, hostnames, and LAN addresses. Use placeholders
  such as `<user>` and RFC 5737 documentation address ranges in examples.
- Do not freeze test counts in prose; point readers to CI for current results.
- Make behavioral claims match code and cite `file:line` in review reports so they are checkable.
- When an ADR supersedes another, update status and supersession links in both directions.
- Keep entries under CHANGELOG `[Unreleased]` until tag time and follow Keep a Changelog.
