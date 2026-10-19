# Sunrise File Manager v1.8.1 — Explorer UI / guided organizing / memo-associated moves

## Actual code changes

1. `MainWindow.xaml`, `ExplorerPane.xaml`, `App.xaml`: Windows Explorer-inspired command group spacing, input line alignment, details-column ordering (name / modified / type / size), active layout selection state, and differentiated command button surfaces. The product is still a WPF explorer implementation, not the Windows shell itself.
2. `FileInspector`: retains the previously requested **right-docked in-main-window** preview, file details above, and file-bound memo below. Preview toggles ON/OFF. The retained `PreviewWindow` sources are used for legacy previews/other tests; the explorer's preview button does not open a dialog.
3. `AiCommandPanel`: a local-first, explicit workflow: select items, choose a common organizer option, pick a destination, preview planned changes, approve once, execute. User-authored command and Gemini remain optional advanced functions. A selection change invalidates a pending approval.
4. `FileMemoStore.MoveFileWithMemos`: prepares renamed `.sunmemo` payloads (including new embedded source name), moves the original file, publishes all memo records, then cleans original memo records. If publication fails and the source can be rolled back, no original memo is intentionally deleted. Existing destination memo records and unreadable source memo records cause fail-closed behavior.
5. `FileActionsService.MoveEntry` is used by user renaming, internal panel transfer, clipboard cut/paste, drag/drop, the organizer engine, and AI undo. Directory moves inherently carry their nested `#memo` directories.
6. Unsaved memo prompts before rename / recycle / move / organizer approval, so a draft is saved or explicitly discarded before an operation changes a path.

## How to use file organizing

1. In an explorer panel select one or more items; Ctrl+A selects all.
2. Click **☷ 파일 정리 (F8)**. The upper line shows the count of selected items.
3. Choose a common action such as **파일 유형별 분류하여 이동**, or use advanced text commands.
4. Click **대상 폴더 지정…** and choose where category subfolders will be created.
5. Click **③ 정리 결과 미리보기**, inspect before/after paths, then **④ 확인하고 실행**.
6. For simple two-pane copy/move use the existing pane transfer bar. These operations also route through the shared movement owner.

## Failure behavior / limits

- No automatic source overwrite. If a target memo collides, or a source memo cannot be read, the file move is blocked *before changing the source file*.
- For a mid-operation I/O error, the source file is moved back if Windows permits; errors in restoring the source are surfaced with both source and destination. No external Windows Explorer operations are observed: only file changes initiated **within Sunrise File Manager** move accompanying memos.
- Unexpected process/OS termination between a file move and sidecar commit cannot be strictly transactional across multiple files; back up important folders. Previous memos remain until new memo records are published.
- Copies do not automatically duplicate memo metadata: memo association is designed for **moves and renames**. Moving a folder carries any internal `#memo` folders as part of the directory tree.
- `.sunmemo` is a Sunrise-specific binary format with compression and signature, **not encryption**.
- Display alignment / accessibility / DPI scaling and C# compilation need real Windows/.NET 10 verification; none is claimed here.

## Tests and status

- Python source contract + XAML/XML parsing checks run in authoring container.
- Targeted C# regression tests added for rename, inter-panel move, directory move, corrupted memo, memo collision, rewritten metadata and preserved original contents.
- Windows WPF executable build, native smoke and GUI screenshot checks: **NOT RUN** (Windows / .NET 10 unavailable in authoring container).
- `BUILD_ON_WINDOWS.cmd` uses `-RunFullTests` to establish fresh source evidence before publishing; prior source PASS evidence is explicitly stale.
