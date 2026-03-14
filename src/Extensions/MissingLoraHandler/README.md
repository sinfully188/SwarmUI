# Missing LoRA Handler Extension

This extension adds opinionated recovery behavior for selected LoRAs that are referenced in parameters or image metadata but are no longer present in the current LoRA model list.

## What stays in core

The core changes were kept intentionally small and frontend-only so the base SwarmUI experience can safely represent missing LoRAs even when this extension is not installed.

- `src/wwwroot/js/genpage/gentab/loras.js`
  Adds missing-state tracking for selected LoRA chips, keeps temporary select options alive for stale entries, and exposes small extension hooks:
  `registerChipActionRenderer(...)` to let extensions render chip actions, and `replaceSelectedLora(...)` to let extensions replace a selected LoRA without mutating core state directly.
- `src/wwwroot/js/genpage/gentab/currentimagehandler.js`
  Preserves missing LoRA entries when reusing parameters from image metadata, including matching weights and section confinements.
- `src/wwwroot/css/genpage.css`
  Adds the minimal visual treatment for missing LoRA chips and the action slot used by extensions.

These core changes were needed because the extension should not own the base concepts of:

- a selected LoRA being missing,
- stale selected LoRAs remaining editable/removable,
- parameter reuse preserving missing selections,
- or core UI state being mutated from extension code internals.

Without that core support, the extension would need to patch private UI state or the selected LoRA would disappear from the visible/editable UI.

## What this extension does

This extension adds the opinionated resolution workflow on top of the core behavior.

- It renders an `Auto Find/Fix` action on missing LoRA chips.
- It calls the `ResolveMissingLora` API route to search for a likely replacement LoRA.
- When a match is found, it asks core to replace the selected missing LoRA with the resolved LoRA while preserving the rest of the chip state.
- It refreshes parameter values after a fix so the UI, browser state, and selected LoRA list stay aligned.

## Core versus extension boundary

The intended split is:

- Core owns selected-LoRA state, missing-state rendering, parameter synchronization, and public helper methods for extensions.
- This extension owns the policy decision to auto-search for replacements and the UI action that triggers that search.

That boundary keeps the default SwarmUI behavior understandable while still allowing opinionated missing-LoRA workflows to live entirely in the extension.