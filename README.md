<img width="1344" height="768" alt="Title" src="https://github.com/user-attachments/assets/ada996fb-730e-400a-826a-c614b0bd6087" />

Language: English | [简体中文](README-CN.md)

# NAI Utility Tool

NAI Utility Tool is a Windows desktop client for NovelAI image workflows. It is built with .NET 9, WinUI 3, Windows App SDK, Win2D, SkiaSharp, and ONNX Runtime. It is not intended to replace the official NovelAI website; instead, it brings day-to-day local workflows into one tool: generation, image-to-image editing, mask editing, prompt management, reference-image workflows, batch automation, post-processing, upscaling, metadata inspection, and local reverse tagging.

The fork version remains `1.3.0_fork.1`. This document describes the **dev stage**, not a new official release.

## Dev goal and status (2026-10-17)

**The dev branch aims to align original-client interactions, effective generation requests, generated results and original-image Info reproduction with the NovelAI website, while retaining local extensions.** This stage uses the captured website build `78a367e` as its reference. Completing this cleanup does not establish identical behavior for every model, input and backend result.

### Stage changelog

- Official hash/effort and Info import without embedded local drafts or sidecars; in-session High/Medium drafts and adjustable Medium CFG Scale remain.
- Complete Quality/UC presets and reversible Text/automatic dialogue, mix and LF handling across editing, import and requests; unused legacy preset code removed.
- Simple/Debug business diagnostics with credential redaction and safe startup ordering. Simple keeps at most seven UTC dates and 8 MiB; Debug records input, effective requests, responses and parse/save stages, without equating HTTP success with saved output.
- Original output bytes retained separately from temporary WebP-to-PNG display conversion; monthly folders and creation-time history, independent of previous-image/send-to navigation and prompt/seed naming.
- Model defaults, legal schedules, independent advanced settings, effective duplicate protection and character action/filter/coordinate handling.
- I2I/Inpaint seed behavior, full-size API masks, feathered composition and raw/derived candidates; selected-source Enhance bytes; aspect-aware Precise Lanczos resizing and stealth cleanup.

### Confirmed fixes and confidence

- **Verified on bounded captured samples:** non-zoom Inpaint API-mask RGBA and composed RGB match; opaque same-source Precise pixels and real stealth cleanup; WebP response and offline display paths. These do not prove every transparency, size or mode.
- **Implemented / method-level checked, not fully live-validated:** Medium hash restoration, preset/Text direct reuse and selective import, LF, Enhance source bytes, defaults/schedules, duplicate guards and character actions. Info reproduction is not signature verification; modified pixels or Info must not be presented as verified signed originals.

### Confirmed unresolved

Disabled characters are still filtered before the website-style state rewrite. Some recorded same-condition generation comparisons still differ; the cause has not been fully isolated, so they are not attributed to prompt handling or a different checkpoint without evidence.

### Not deeply investigated / next TODO

- Character/Text/history: provide model/effort, enabled states, exact action order and one shareable minimal prompt with expected/actual results.
- Remaining generation differences: provide the same source image, seed, dimensions, model/effort, effective parameters and both original outputs.
- Brush/ROI: provide source dimensions, canvas zoom, brush value, ROI rectangle, character coordinates and expected final image.
- Transparency/reference/role-count boundaries: provide one minimal original sample, model, relevant settings/count and observed failure. Full transparency and website role-limit disagreements remain insufficiently investigated.

Website mask units have been measured, but their mapping to the client brush UI needs confirmation. Focused-region sampling/cropping, character reprojection and final placement, plus some Text/history combinations, have not completed full live verification. Reuse existing evidence before collecting only the missing conditions. Website-only PixelSnap/3D capabilities are not automatically in scope. Debug records never include API keys, but business records can contain prompts, images and paths: sanitize reports again and share only the minimal reproduction, not private logs, HARs or credentials.

<img width="1455" height="940" alt="Preview" src="https://github.com/user-attachments/assets/88c9a0d3-5fcb-464f-80bf-98075fc9f3f6" />

## Features

### Image Generation

- Supports NovelAI Diffusion 5, 4.5, 4, and 3 model families.
- Supports common samplers and schedulers, with incompatible options filtered according to the selected model.
- Supports separate positive, negative, and style prompt inputs.
- Supports NAI v4+ character prompts, character negative prompts, and character positions: up to 22 characters for V5 and 6 for V4/V4.5.
- Main, style, and character prompt boxes support custom heights through a bottom-right corner shown on hover. Drag to resize or double-click to restore automatic height. Main and style heights are saved automatically; character heights follow the remembered-character setting. Restore all default prompt heights with one click under Settings > Usage.
- Supports seed management, Variety+, quality tags, UC presets, CFG Scale, CFG Rescale, steps, and other advanced parameters.
- Supports post-generation preview, save, copy, delete, routing to other workspaces, and generation history management.
- Favorite saved output images using the star in gallery/history thumbnails or the history/preview context menu. Stars follow the system accent color; favorites are retained across restarts.
- The gallery provides a date picker, image-size slider, and Favorites only filter. Unfavoriting an image keeps it in the current filtered view until you reload favorites by toggling the filter, changing the date, or returning to the gallery.
- Enhance supports upscale amounts, five magnitude levels, individual strength/noise settings, and V5 Max; its confirmation button shows the Anlas cost for the current settings.
- Includes continuous generation and duplicate-request protection.

### Image-to-Image and Mask Editing

- Supports masked inpainting and denoise-based image-to-image modes.
- Includes a Win2D canvas with brush, eraser, rectangle mask, undo, redo, zoom, and pan tools.
- Supports mask preview, invert, expand, shrink, clear, fill transparent area, crop canvas, and image alignment.
- Can restore parameters from image metadata and send them back to the image-to-image workspace.
- Supports canvas and mask export.

### Prompt Workflow

- Tag completion is loaded from `assets/tagsheet/`.
- Supports prompt weight highlighting, prompt normalization, prompt shortcuts, and random style tags.
- Supports a Wildcards drawer, using explicit `__name__` syntax by default with configurable behavior in settings.
- Supports conversion between classic NAI weights, NAI 4+ numeric weights, and SD-WebUI weight syntax.
- Supports prompt-generation assistance through the NovelAI text API.

### Reference Images and Vibe

- Supports official NAI Vibe Transfer.
- Supports official NAI Precise Reference, with character, style, and character-plus-style reference types.
- Supports local Vibe pre-encode caching to avoid repeated encoding cost.
- Includes a Vibe pre-encode manager for viewing, cleaning, relocating source images, and exporting encoded files.
- Can optionally copy Vibe source images into the local workspace for long-term reference management.

### Automation

- Supports saving and loading automation presets.
- Supports request count limits, request intervals, and HTTP error retry policies.
- Supports randomized sizes, prompt shortcuts, style tags, and Vibe selections.
- Supports automatic upscaling after generation.
- Supports automatically applying a post-processing preset chain after generation.

### Post-Processing

- Supports effect-chain editing, ordering, undo, redo, preset saving, and preset application.
- Built-in effects include brightness/contrast, saturation/vibrance, temperature, glow, radial blur, vignette, chromatic aberration, noise, Gamma, pixelate, solid block, scanline, and JPEG loss.
- Preview and export are handled by a unified post-processing render service. It prefers the Win2D GPU path and falls back to the CPU path for effect chains that are not suitable for GPU rendering.
- Pixelate and solid block support region editing directly in the preview area.

### Local Upscaling

- Uses ONNX Runtime DirectML for local upscaling, with GPU/CPU preference configurable in settings.
- Bundles anime-oriented upscaler models under `models/upscaler/`.
- Supports slider or direct numeric target-scale input, multi-pass processing toward the target scale, and tiled inference to reduce memory pressure on large images.

### Inspection, Metadata, and Reverse Tagging

- Supports dragging in or opening images to inspect NovelAI metadata.
- Can restore prompts, character prompts, character coordinates and custom-position settings, parameters, and reference-image information back into generation or image-to-image workspaces.
- Image parameter imports let you select prompts, characters, model, generation settings, size, seed, and references independently. Seeds are unchecked by default. Characters can be replaced or appended, and recorded actual prompts can be imported.
- The inspect page has separate Tagger and Import actions. The import dialog ends with Send to generation, Send to image-to-image, and Cancel buttons. The image import checkbox stays visible and only applies when sending to image-to-image. Leaving it unchecked preserves the canvas and imports only the selected parameters.
- Supports removing metadata on save and global metadata stripping.
- Supports image obfuscation and de-obfuscation.
- Can optionally configure a local ONNX reverse tagger model for image tag inference.
- Supports common WD14, SmilingWolf, and PixAI Tagger model directory formats by reading tag definitions from `selected_tags.csv`.
- Separates reverse-tagging results into general tags, character tags, and rating tags, with configurable thresholds and optional rating-tag inclusion in generated prompts.

### Local Experience

- Supports Simplified Chinese, Traditional Chinese, English, Japanese, Korean, Russian, German, French, Spanish, and Latin UI resources.
- Supports light mode, dark mode, system theme, and transparency options.
- Supports SuperDrop: drag an image into the window and choose generation prompt, Vibe, Precise Reference, image-to-image, upscale, post-processing, or inspection targets.
- API tokens are encrypted with Windows DPAPI and stored under the current local user.
- Supports privacy mode, where generated results are not automatically written to `output/` or generation history.
- The default delete behavior moves files to the system recycle bin; settings can switch this to permanent deletion.

## Models and Parameters

Generation models:

- `nai-diffusion-5-full`
- `nai-diffusion-5-curated`
- `nai-diffusion-4-5-full`
- `nai-diffusion-4-5-curated`
- `nai-diffusion-4-full`
- `nai-diffusion-4-curated-preview`
- `nai-diffusion-3`
- `nai-diffusion-furry-3`

Inpainting models:

- `nai-diffusion-5-full-inpainting`
- `nai-diffusion-4-5-full-inpainting`
- `nai-diffusion-4-5-curated-inpainting`
- `nai-diffusion-4-full-inpainting`
- `nai-diffusion-4-curated-inpainting`
- `nai-diffusion-3-inpainting`
- `nai-diffusion-furry-3-inpainting`

`nai-diffusion-5-curated-inpainting` is intentionally not listed because the
model is not currently available for generation.

Samplers:

- `k_euler_ancestral`
- `k_euler`
- `k_dpmpp_2m`
- `k_dpmpp_sde`
- `k_dpmpp_2s_ancestral`
- `k_dpm_2`
- `k_dpm_fast`
- `k_dpmpp_2m_sde`
- `ddim`
- `ddim_v3`

Schedulers:

- `native`
- `karras`
- `exponential`
- `polyexponential`

## Requirements

- Windows 10 or Windows 11.
- .NET SDK `9.0.301` or a compatible newer feature-band SDK when building from source.
- Windows App SDK build support, usually provided by Visual Studio 2022 and related Windows development components.
- A NovelAI API token for generation, image-to-image, Vibe encoding, prompt generation, account quota, and other online features.
- Optional: a local ONNX reverse tagger model directory for image tag inference. The model directory must contain a `.onnx` model and `selected_tags.csv`.

Check the local SDK:

```powershell
dotnet --info
```

## Repository Layout

```text
NAITool/
|-- NAITool.sln
|-- src/
|   |-- NAITool/             Main WinUI 3 desktop application
|   `-- NAIToolLauncher/     Outer launcher executable
|-- assets/
|   |-- fxpresets/           Default post-processing presets
|   |-- i18n/                Multilingual UI resources
|   |-- icon/                Application icon
|   |-- img/                 Bundled app images
|   |-- splash/              Splash screen assets
|   |-- svg/                 SVG resources used by the UI
|   |-- tagsheet/            Tag completion and style data
|   `-- wildcards/           Bundled wildcard resources
|-- models/
|   `-- upscaler/            Bundled local upscaler models
|-- publish.ps1              Local publish script
|-- Directory.Build.props    Shared build output layout
|-- global.json              .NET SDK version policy
|-- CONTRIBUTING.md
|-- SECURITY.md
`-- LICENSE
```

## Build

For development builds, use `Debug | x64`:

```powershell
dotnet build .\NAITool.sln -c Debug -p:Platform=x64
```

A successful build creates the development runtime layout:

```text
build/Debug/
|-- NAI Utility Tool.exe     Launcher entry point
|-- bin/                     Main application and dependencies
|-- assets/                  Junction to repository assets
|-- models/                  Junction to repository models
|-- user/
|-- output/
`-- logs/
```

To build only the main application:

```powershell
dotnet build .\src\NAITool\NAITool.csproj -c Debug -p:Platform=x64
```

## Run

After a full build, run the launcher from the repository root:

```powershell
.\build\Debug\NAI Utility Tool.exe
```

You can also open `NAITool.sln` in Visual Studio or Rider and run `src/NAITool/NAITool.csproj` for debugging.

On first launch, the quick tour can configure language, NovelAI API token, asset-protection mode, and the optional reverse tagger model directory.

## Publish

Use the included script to create a local publish package:

```powershell
.\publish.ps1
```

By default, it publishes `Release`, `win-x64` to:

```text
publish/NAI Utility Tool/
|-- NAI Utility Tool.exe     Launcher entry point
|-- bin/                     Main application and dependencies
|-- assets/                  Runtime assets
|-- models/                  Bundled upscaler models
|-- user/                    Default user data directories and presets
|-- output/
`-- logs/
```

You can specify configuration and runtime:

```powershell
.\publish.ps1 -Configuration Release -Runtime win-x64
```

Declared project runtimes include:

- `win-x64`
- `win-x86`
- `win-arm64`

## Runtime Data and Privacy

The app writes runtime data beside the executable so development and published layouts behave consistently. The following directories and files are local data, generated output, or sensitive configuration and should not be committed:

- `user/config/settings.json`
- `user/config/apiconfig.json`
- `user/fxpresets/`
- `user/userprompts/`
- `user/wildcards/`
- `user/automation/`
- `user/vibe/`
- `output/`
- `logs/`
- `build/`
- `publish/`

`user/config/apiconfig.json` stores API tokens and cached account information. The current version encrypts tokens with Windows DPAPI, but the file is still sensitive and should not be uploaded, screenshotted publicly, or attached to issue logs.

Privacy-related options:

- Privacy mode: generated results are not automatically saved to `output/` and are not added to history.
- Global metadata stripping: removes metadata automatically when saving images.
- Delete behavior: choose between moving files to the system recycle bin or permanent deletion.
- Developer logs: enable only when troubleshooting, and check logs for tokens, account data, or private local paths before submitting issues.

## Models and External Assets

The repository includes small local upscaler models under `models/upscaler/` for the built-in upscale workflow.

Local reverse tagger models are not part of the repository. The recommended default is to place user-downloaded reverse tagger models under `models/tagger/` or another local directory, then select that directory in settings. The model directory must contain a `.onnx` model and `selected_tags.csv`; the app reads the CSV column layout to support common WD14, SmilingWolf, and PixAI Tagger tag tables. This directory is excluded by `.gitignore` because models can be large and may have separate license terms.

The tagsheet, wildcards, presets, and other resources under `assets/` may come from third-party or derived data. If you redistribute this application, package it into another project, or use it commercially, review the original source licenses and redistribution terms for those resources and any additional models.

## Contributing

When opening issues, please include:

- Reproduction steps
- Expected behavior and actual behavior
- Screenshots or screen recordings
- Relevant log snippets
- Windows version, .NET SDK version, GPU, and driver information

Before submitting PRs, please confirm:

- Changes are focused and do not include unrelated formatting or runtime data.
- At least one local build verification has completed.
- README or related documentation is updated when the change affects user behavior.
- `publish.ps1` is rechecked when the change affects the publish layout.

Do not commit:

- `user/`
- `output/`
- `logs/`
- `build/`
- `publish/`
- API tokens, account information, private local paths, or downloaded large model files

See `CONTRIBUTING.md` and `SECURITY.md` for more details.

## License

This project is licensed under GPL-3.0. See `LICENSE` for the full license text. If you use this project's source code or design as a reference, please provide attribution and link back to this repository.

---

## Fork: 1.3.0_fork.1

Modified from [Aeka0/NAI-Utility-Tool](https://github.com/Aeka0/NAI-Utility-Tool) **Release 1.3.0**. Upstream feature and usage references are retained; the current dev goal and stage status are documented above.

### Updates
- V5 High/Medium support, with Medium's fixed sampling parameters and restoration of High settings.
- Custom gateway API endpoints, including prompt generation; configurable generation wait time (60–300 s).
- Anlas cost and estimated image counts, V5 quota display and refresh.
- History-thumbnail blur toggle and a compact, initially collapsed result toolbar.

### Fixes in [Issue #1](https://github.com/Andy-linux/NAI-Utility-Tool/issues/1)
- **Account and billing (1, 8, 13):** V5 quota/Anlas fallback, feature-specific API routing, consistent costs and balances.
- **Responses and timeouts (3, 9, 10):** streaming final/error handling, ZIP result selection, premature request cancellation.
- **Saving and metadata (2, 4, 7):** unsaved workspace alerts, atomic settings writes, separate SMEA/Variety+ metadata.
- **Models and parameters (5, 6, 11, 12):** partial-download detection, Variety+ capability, Quality/UC presets, V5 High defaults.

This is not an official upstream release. Modified source and the [GPL-3.0 license](LICENSE) remain available in this repository. Third-party models and data retain their own terms.
