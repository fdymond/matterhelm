# Agent instructions

`CLAUDE.md` is the binding playbook for every coding agent, including Codex,
Gemini, and Cursor. Read it first and follow it exactly.

Merge gates:

```powershell
npm --prefix bridge run verify
& "C:\Program Files\dotnet\dotnet.exe" build app/MatterHelm/MatterHelm.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release
```
