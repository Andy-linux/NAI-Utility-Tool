# Installation and fork.1 upgrade regression

`Verify-Upgrade.ps1` runs the actual compiled Setup against the published fork.1 ZIP and the clean new payload. Use PowerShell 7, the .NET 9 SDK, and a fresh test directory. The registered-install cases require that this Windows account has no existing NAI Utility Tool uninstall entry or Start menu shortcut, so tests cannot displace a real installation.

```powershell
.\tests\Installer\Verify-Upgrade.ps1 -Setup "new Setup.exe" -BaselineZip "published fork.1.zip" -Payload "clean new portable folder" -OlderFork2 "old fork.2 portable folder" -TestRoot "new empty test path"
```

The old fork.1 assembly writes its own settings and DPAPI-encrypted synthetic credential. The new assembly must read them with the same values after installation. Neither probe makes network requests or uses real account credentials.

Cases cover fork.1 portable upgrade, repeat installation, locked-file rejection, accidental bin-directory selection, clean portable extraction, replacement of old fork.2 with the same file version, registered installation/upgrade, and uninstall retention. Seeded user presets, wildcard edits, prompts, reference/cache files, outputs, logs, customized bundled resources and downloaded-model placeholders are compared before and after; cache/model comparisons establish byte retention, not inference validation.

Results and installer logs stay in TestRoot. Registered test copies are uninstalled at the end, retaining data for inspection; portable copies are kept. Run the ordinary Seeds regression against the final published DLL separately. Native wizard layout and first-launch UI are separate visual checks.
