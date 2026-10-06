# Security policy

SnapBack runs with the user's own rights and is designed never to affect system, elevated, protected
or other users' processes (see [docs/SAFETY.md](docs/SAFETY.md)).

If you find a way to make SnapBack close or otherwise affect a process it should protect, to make it
force-terminate something without explicit confirmation, or any other security issue, please report
it privately through **GitHub Security Advisories** ("Report a vulnerability" on the Security tab)
instead of opening a public issue. Please include the Windows version, steps to reproduce and the
relevant part of the log (`%LOCALAPPDATA%\SnapBack\logs`).

Only the latest release receives security fixes.
