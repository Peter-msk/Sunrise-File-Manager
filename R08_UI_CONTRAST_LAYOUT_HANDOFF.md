# Sunrise File Manager R08 — UI contrast / layout handoff

Source baseline: R07 company full source; every original file preserved except listed UI and test changes.

## Changes
- 2–4 panes now respect each pane's saved `TreeVisible` property; per-pane `탐색 창` menu/toolbar button toggles it independently. In compact split views, the tree starts at 150 DIP wide when enabled, 215 otherwise.
- AI dock has a visible top-right `✕ 닫기` action, blocked during active file/AI operations. Ollama installation guide dialog has its own `닫기` action and cancels any active installation cancellation token.
- File-list `DataGrid` has explicit column resizing enabled; column header right-edge separators are thicker, themed and hover-highlit while leaving WPF's built-in resize grippers and sorting logic intact.
- Title bar now shows only split-mode buttons, AI file cleanup and a themed `설정 · 기능` drop-down. Preview, inspector, Ollama connection, split layout cycling and light/dark selection moved into that menu, while code-behind compatible controls remain hidden in the visual tree.
- Dark mode now changes semantic file-name colors; existing rows trigger `NameForeground` refresh on theme selection. Dynamic brush references are applied across UI views; accent-button text has a separate contrast-aware palette key.

## Requirements preserved
- SecureDisk company build policy (document fixture tests skipped; no Office/PDF fixture shipped in company archive)
- Self-contained Windows x64 OneDir packaging via `BUILD_ON_WINDOWS_COMPANY.cmd`
- `#memo`, explorer operations, multi-pane layouts, docked full-height preview and local Ollama functions.

## Verification status
- Python regression/contract validation and XAML parsing should run on source only.
- The environment here is Linux without dotnet, WPF or SecureDisk; Windows compilation and live UI smoke screenshots are NOT validated.
- Do not market this ZIP as an already compiled Windows EXE.
