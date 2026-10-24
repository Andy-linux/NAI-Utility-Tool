# Release update checks

Run `dotnet run --project tests/Updates -c Release` with the .NET 9 SDK. The tests compile the production release service directly and use a controlled HTTP handler. No real installer is executed.

Checks cover numeric fork-version ordering, draft/prerelease and missing-asset filtering, release discovery, byte-exact downloads, GitHub digest and checksum-file fallback, hash/size failures, missing or mismatched checksums, unrelated download URLs, cancellation before and during transfer, and installed/portable handoff arguments with a Chinese path containing spaces.

The actual GitHub connection, WinUI dialog, saved settings, normal process exit, Inno parent wait and data retention require separate integration checks. See `installer/UPDATES.md` and `tests/Installer`.
