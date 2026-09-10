# Getting help

- **Setup or usage question?** Start with the
  [user guide](docs/user-guide.md) — it covers install, the one-time Google
  Home Developer Console step, pairing, every setting, troubleshooting, and
  privacy. Natural voice phrasing lives in [docs/routines.md](docs/routines.md).
- **Something broken?** Open a
  [bug report](https://github.com/fdymond/matterhelm/issues/new/choose) and
  attach a diagnostics bundle (tray → **Settings…** → **Advanced** → **Export
  diagnostics**). Its manifest omits machine/user identity and paths; bundled
  logs redact commissioning credentials, machine/user names, profile paths,
  and IP literals; and `config.json` is sanitised by default. Review it before
  sharing; see the user guide's
  [Privacy notes](docs/user-guide.md#privacy-notes).
- **Idea or missing feature?** Open a feature request via the same issue
  chooser.
- **Security vulnerability?** Never a public issue — see
  [SECURITY.md](SECURITY.md) for private reporting.

MatterHelm is maintained in spare time; issues are triaged best-effort.
Please search existing issues before opening a new one.
