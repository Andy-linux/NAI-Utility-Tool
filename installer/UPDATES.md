# Maintenance updates

The client checks GitHub Releases for `Andy-linux/NAI-Utility-Tool` only, when the user opens **Help → Check for updates**. Maintainers review and integrate original-project changes before publishing a compatible fork release. There is no startup polling, background installation or arbitrary patch execution.

## Release contract

1. Increment `Version`, `FileVersion` and `InformationalVersion` in `src/NAITool/NAITool.csproj`. Stable tags use `1.3.0_fork.N`; the client compares the numeric fork revision, so `fork.10` follows `fork.9`. Update the fallback version in `installer/NAITool.iss` and the version shown in `INSTALL.txt`.
2. Build the committed source with `publish.ps1 -Destination <fresh directory>`, then pass that directory to `build-installer.ps1`. Setup and the optional ZIP/7z must use the same clean payload. Keep `user/`, `output/` and `logs/` out of releases.
3. Verify image import and updater checks, plus a real prior-release upgrade in an isolated folder. Confirm user data and customized resources remain intact. `tests/Installer/Verify-Upgrade.ps1` covers installed/portable upgrades and locked targets. `/UPDATEPID` additionally requires a parent-exit handoff check.
4. Create a new stable GitHub Release/tag, with `NAI.Utility.Tool.<tag>.Setup.exe`. Do not replace a release under an unchanged tag: the client treats equal versions as already installed. Write user-facing changes in the release body.
5. Publish `SHA256SUMS.txt` with SHA-256 plus the exact asset filename on each line. The client prefers GitHub's `sha256:` asset digest and otherwise uses this release's checksum file. Compare the uploaded asset sizes/digests with the local files before marking the release ready.
6. Update the current-release link in both READMEs when that release is actually published.

Drafts, prereleases and releases without the correctly named Windows x64 Setup are not install candidates. Downloads are streamed into a temporary `.partial` file and become installable only after exact size and SHA-256 checks. Cancellation and verification failure do not return an installer. These checks establish download integrity; they are not Authenticode publisher signatures.

## Installation handoff

The user chooses a release, downloads it, then explicitly requests installation. Active generation/inference and a failed settings save block the handoff. Existing unsaved-work confirmation remains in effect. Setup receives the actual application root, `/PORTABLE=1` for portable folders (or `0` for registered installations), `/UPDATEPID=<client PID>` and `/NORESTART`.

`/PORTABLE` and `/UPDATEPID` are project-specific Inno script parameters. Setup waits up to 30 seconds for the initiating client to exit, then runs the existing destination and file-lock checks before copying. It never terminates a process. `user/`, `output/`, `logs/` and existing `assets/`/`models/` remain preserved. The wizard still lets the user review or cancel installation.

fork.2 has no update UI. Its first upgrade to fork.3 requires downloading and running Setup once; subsequent versions can be selected inside the client. A local/mock check of a future release is not evidence of a published online fork.3-to-fork.4 upgrade.
