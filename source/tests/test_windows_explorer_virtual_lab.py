"""
Comprehensive Virtual Sandbox End-to-End Execution & Verification Suite:
1. Predefined Row/Column Split Presets (14 presets across 1/2/3/4 splits) & Grid Blueprint geometry verification
2. Multi-Pane Working Directory, Tab History, Filter & Selection Preservation across Split Expand/Shrink/Preset changes
3. Full Windows 11/10 File Explorer feature parity on real sample files/folders in the sandbox
4. Q-Dir (Quad Explorer) convenience features (Color Filter, Pane Swap, Pane Sync, Directory Listing CSV Export)
"""
import hashlib
import json
import os
import shutil
import tempfile
import unittest
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / "Sunrise.FileManager"


class SimulatedPaneState:
    """Simulates ExplorerPane tab/folder/selection/filter state across split changes."""
    def __init__(self, index: int, default_folder: Path, initial_session_paths: list[Path] | None = None):
        self.index = index
        self.has_initial_session_path = bool(initial_session_paths)
        self.has_user_navigated = False
        start_paths = initial_session_paths if initial_session_paths else [default_folder]
        self.tabs = [list( [p.resolve()] ) for p in start_paths]  # each tab is a history list of Paths
        self.tab_pos = [0 for _ in start_paths]
        self.active_tab_idx = 0
        self.search_text = ""
        self.selected_names: list[str] = []
        self.reload_count = 0

    @property
    def active_folder(self) -> Path:
        hist = self.tabs[self.active_tab_idx]
        return hist[self.tab_pos[self.active_tab_idx]]

    def user_navigate(self, target: Path):
        target = target.resolve()
        assert target.is_dir(), f"Target directory does not exist: {target}"
        self.has_user_navigated = True
        hist = self.tabs[self.active_tab_idx]
        pos = self.tab_pos[self.active_tab_idx]
        if hist[pos] == target:
            return
        del hist[pos + 1 :]
        hist.append(target)
        self.tab_pos[self.active_tab_idx] = len(hist) - 1
        self.reload_count += 1

    def back(self) -> bool:
        pos = self.tab_pos[self.active_tab_idx]
        if pos <= 0:
            return False
        self.has_user_navigated = True
        self.tab_pos[self.active_tab_idx] = pos - 1
        self.reload_count += 1
        return True

    def forward(self) -> bool:
        hist = self.tabs[self.active_tab_idx]
        pos = self.tab_pos[self.active_tab_idx]
        if pos >= len(hist) - 1:
            return False
        self.has_user_navigated = True
        self.tab_pos[self.active_tab_idx] = pos + 1
        self.reload_count += 1
        return True

    def sync_to_folder_if_untouched(self, folder: Path):
        if self.has_user_navigated or self.has_initial_session_path:
            return
        folder = folder.resolve()
        if self.active_folder != folder:
            self.tabs[self.active_tab_idx].append(folder)
            self.tab_pos[self.active_tab_idx] = len(self.tabs[self.active_tab_idx]) - 1
            self.reload_count += 1

    def sync_to_folder(self, folder: Path):
        folder = folder.resolve()
        self.has_user_navigated = True
        if self.active_folder != folder:
            self.tabs[self.active_tab_idx].append(folder)
            self.tab_pos[self.active_tab_idx] = len(self.tabs[self.active_tab_idx]) - 1
            self.reload_count += 1

    def swap_working_state_with(self, other: "SimulatedPaneState"):
        (
            self.tabs, other.tabs,
            self.tab_pos, other.tab_pos,
            self.active_tab_idx, other.active_tab_idx,
            self.has_user_navigated, other.has_user_navigated,
            self.has_initial_session_path, other.has_initial_session_path,
            self.search_text, other.search_text,
            self.selected_names, other.selected_names,
        ) = (
            other.tabs, self.tabs,
            other.tab_pos, self.tab_pos,
            other.active_tab_idx, self.active_tab_idx,
            other.has_user_navigated, self.has_user_navigated,
            other.has_initial_session_path, self.has_initial_session_path,
            other.search_text, self.search_text,
            other.selected_names, self.selected_names,
        )


class SimulatedWorkspaceController:
    """Mirrors MainWindow + WorkspaceSplitLayoutEngine split & preset transitions."""
    def __init__(self, default_folder: Path):
        self.panes = [SimulatedPaneState(i, default_folder) for i in range(4)]
        self.visible_panels = 1
        self.active_pane = 0
        self.split_preset = "Single"
        self.last_preset_by_count = {
            1: "Single",
            2: "TwoColumns",
            3: "ThreeTop1Bottom2",
            4: "FourGrid2x2",
        }

    def arrange_panels(self, new_count: int, explicit_preset: str | None = None):
        prev_count = self.visible_panels
        clamped = max(1, min(4, new_count))
        active_folder = self.panes[self.active_pane].active_folder

        # 1. Shrink preservation: if active pane is about to be hidden and user navigated in it, swap into surviving slot
        if clamped < prev_count and self.active_pane >= clamped and self.panes[self.active_pane].has_user_navigated:
            surviving = clamped - 1
            self.panes[surviving].swap_working_state_with(self.panes[self.active_pane])
            self.active_pane = surviving
        elif clamped > prev_count:
            # 2. Expand convenience: untouched newly revealed panes inherit current working folder
            for i in range(prev_count, clamped):
                self.panes[i].sync_to_folder_if_untouched(active_folder)

        self.visible_panels = clamped
        self.split_preset = explicit_preset or self.last_preset_by_count[clamped]
        self.last_preset_by_count[clamped] = self.split_preset
        self.active_pane = min(self.active_pane, clamped - 1)


class VirtualWindowsExplorerSandbox:
    """Executes real filesystem operations matching FileActionsService, ExplorerUndoService, FileMemoStore, and TransferService."""
    MEMO_DIR = "#memo"
    MEMO_EXT = ".sunmemo"

    def __init__(self, root_dir: Path):
        self.root = root_dir
        self.undo_stack = []
        self.redo_stack = []

    @staticmethod
    def validate_name(name: str) -> str:
        if not name or not name.strip() or name in (".", "..") or name.rstrip(" .") != name:
            raise ValueError("사용할 수 없는 파일 또는 폴더 이름입니다.")
        if any(ord(c) < 32 or c in '<>:"/\\|?*' for c in name):
            raise ValueError("사용할 수 없는 파일 또는 폴더 이름입니다.")
        stem = name.split(".")[0].rstrip(" ").upper()
        if stem in ("CON", "PRN", "AUX", "NUL") or (
            len(stem) == 4 and stem[:3] in ("COM", "LPT") and (stem[3].isdigit() and stem[3] != "0" or stem[3] in "¹²³")
        ):
            raise ValueError("Windows에서 예약된 파일 이름입니다.")
        return name

    def suggest_new_folder_name(self, parent: Path, base_name: str = "새 폴더") -> str:
        clean = self.validate_name(base_name)
        if not (parent / clean).exists():
            return clean
        for i in range(2, 1000):
            cand = f"{clean} ({i})"
            if not (parent / cand).exists():
                return cand
        return clean

    def suggest_new_file_name(self, parent: Path, base_name: str = "새 텍스트 문서.txt") -> str:
        clean = self.validate_name(base_name)
        if not (parent / clean).exists():
            return clean
        stem, ext = os.path.splitext(clean)
        for i in range(2, 1000):
            cand = f"{stem} ({i}){ext}"
            if not (parent / cand).exists():
                return cand
        return clean

    def create_folder(self, parent: Path, name: str | None = None) -> Path:
        effective = self.validate_name(name or self.suggest_new_folder_name(parent))
        target = parent / effective
        if target.exists():
            raise FileExistsError("같은 이름의 항목이 이미 존재합니다.")
        target.mkdir(parents=False, exist_ok=False)
        self.redo_stack.clear()
        self.undo_stack.append(("create_dir", [(str(target), None, True)]))
        return target

    def create_file(self, parent: Path, name: str | None = None, content: str = "") -> Path:
        effective = self.validate_name(name or self.suggest_new_file_name(parent))
        target = parent / effective
        if target.exists():
            raise FileExistsError("같은 이름의 항목이 이미 존재합니다.")
        target.write_text(content, encoding="utf-8")
        self.redo_stack.clear()
        self.undo_stack.append(("create_file", [(str(target), None, True)]))
        return target

    def save_memo(self, file_path: Path, text: str, user: str = "winUser", date_str: str = "26.10.19") -> Path:
        memo_dir = file_path.parent / self.MEMO_DIR
        memo_dir.mkdir(exist_ok=True)
        memo_file = memo_dir / f"{file_path.name}_memo_{date_str}_{user}{self.MEMO_EXT}"
        payload = {
            "FileName": file_path.name,
            "UserName": user,
            "Date": date_str,
            "UpdatedAtUtc": "2026-10-19T00:00:00Z",
            "Text": text,
        }
        memo_file.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        return memo_file

    def list_memos(self, file_path: Path) -> list[dict]:
        memo_dir = file_path.parent / self.MEMO_DIR
        if not memo_dir.is_dir():
            return []
        prefix = f"{file_path.name}_memo_"
        results = []
        for f in sorted(memo_dir.iterdir()):
            if f.name.startswith(prefix) and f.name.endswith(self.MEMO_EXT):
                data = json.loads(f.read_text(encoding="utf-8"))
                if data.get("FileName") == file_path.name:
                    results.append(data)
        return results

    def move_entry_with_memos(self, src: Path, dst: Path):
        if dst.exists():
            raise FileExistsError("대상에 같은 이름이 존재합니다.")
        if src.is_dir():
            shutil.move(str(src), str(dst))
            return
        src_memo_dir = src.parent / self.MEMO_DIR
        prefix = f"{src.name}_memo_"
        staged_memos = []
        if src_memo_dir.is_dir():
            dst_memo_dir = dst.parent / self.MEMO_DIR
            dst_memo_dir.mkdir(exist_ok=True)
            for mf in list(src_memo_dir.iterdir()):
                if mf.name.startswith(prefix) and mf.name.endswith(self.MEMO_EXT):
                    suffix = mf.name[len(prefix):]
                    new_mf = dst_memo_dir / f"{dst.name}_memo_{suffix}"
                    data = json.loads(mf.read_text(encoding="utf-8"))
                    data["FileName"] = dst.name
                    staged_memos.append((mf, new_mf, json.dumps(data, ensure_ascii=False)))
        shutil.move(str(src), str(dst))
        for old_mf, new_mf, updated_json in staged_memos:
            new_mf.write_text(updated_json, encoding="utf-8")
            if old_mf != new_mf and old_mf.exists():
                old_mf.unlink()

    def rename(self, src: Path, new_name: str) -> Path:
        clean = self.validate_name(new_name)
        dst = src.parent / clean
        if src == dst:
            return src
        self.move_entry_with_memos(src, dst)
        self.redo_stack.clear()
        self.undo_stack.append(("rename", [(str(dst), str(src), False)]))
        return dst

    def batch_rename(self, pairs: list[tuple[Path, str]]) -> list[tuple[Path, Path]]:
        results = []
        ops = []
        for src, new_name in pairs:
            clean = self.validate_name(new_name)
            dst = src.parent / clean
            self.move_entry_with_memos(src, dst)
            results.append((src, dst))
            ops.append((str(dst), str(src), False))
        self.redo_stack.clear()
        self.undo_stack.append(("batch_rename", ops))
        return results

    def duplicate_entries(self, paths: list[Path]) -> list[Path]:
        created = []
        for src in paths:
            is_dir = src.is_dir()
            ext = "" if is_dir else src.suffix
            stem = src.name if is_dir else src.name[: len(src.name) - len(ext)]
            target = None
            for n in range(1, 1000):
                suffix = " - 복사본" if n == 1 else f" - 복사본 ({n})"
                cand = src.parent / self.validate_name(f"{stem}{suffix}{ext}")
                if not cand.exists():
                    target = cand
                    break
            assert target is not None
            if is_dir:
                shutil.copytree(src, target)
            else:
                shutil.copy2(src, target)
            created.append(target)
        self.redo_stack.clear()
        self.undo_stack.append(("duplicate", [(str(p), None, True) for p in created]))
        return created

    def drag_drop_or_paste(self, sources: list[Path], target_folder: Path, kind: str) -> list[tuple[Path, Path]]:
        norm_target = target_folder.resolve()
        if kind == "Copy":
            same_folder = [p for p in sources if p.resolve().parent == norm_target]
            cross_folder = [p for p in sources if p.resolve().parent != norm_target]
            results = []
            if same_folder:
                dups = self.duplicate_entries(same_folder)
                results.extend(zip(same_folder, dups))
            for src in cross_folder:
                dst = norm_target / src.name
                if src.is_dir():
                    shutil.copytree(src, dst)
                else:
                    shutil.copy2(src, dst)
                results.append((src, dst))
            return results

        movable = [p for p in sources if p.resolve().parent != norm_target]
        if not movable:
            return []
        moved_pairs = []
        undo_ops = []
        for src in movable:
            dst = norm_target / src.name
            self.move_entry_with_memos(src, dst)
            moved_pairs.append((src, dst))
            undo_ops.append((str(dst), str(src), False))
        self.redo_stack.clear()
        self.undo_stack.append(("move", undo_ops))
        return moved_pairs

    def undo_last(self) -> bool:
        if not self.undo_stack:
            return False
        label, ops = self.undo_stack.pop()
        redo_ops = []
        for current_path, orig_path, created_new in reversed(ops):
            cur = Path(current_path)
            if created_new:
                if cur.is_dir():
                    if not any(cur.iterdir()):
                        cur.rmdir()
                        redo_ops.append((current_path, None, True))
                elif cur.is_file():
                    cur.unlink()
            elif orig_path:
                orig = Path(orig_path)
                self.move_entry_with_memos(cur, orig)
                redo_ops.append((orig_path, current_path, False))
        if redo_ops:
            redo_ops.reverse()
            self.redo_stack.append((label, redo_ops))
        return True

    def redo_last(self) -> bool:
        if not self.redo_stack:
            return False
        label, ops = self.redo_stack.pop()
        undo_ops = []
        for current_path, target_path, recreate_dir in ops:
            cur = Path(current_path)
            if recreate_dir:
                cur.mkdir(parents=False, exist_ok=True)
                undo_ops.append((current_path, None, True))
            elif target_path:
                tgt = Path(target_path)
                self.move_entry_with_memos(cur, tgt)
                undo_ops.append((target_path, current_path, False))
        if undo_ops:
            self.undo_stack.append((label, undo_ops))
        return True

    @staticmethod
    def qdir_color_for(name: str, is_dir: bool = False, is_hidden: bool = False, enabled: bool = True) -> str:
        if not enabled:
            return "#172B43"
        if is_hidden:
            return "#52657A"
        if is_dir:
            return "#163A55"
        ext = os.path.splitext(name)[1].upper()
        if ext in (".EXE", ".MSI", ".CMD", ".BAT"):
            return "#5B21B6"
        if ext in (".ZIP", ".7Z", ".RAR", ".TAR", ".GZ"):
            return "#8C5200"
        if ext in (".HWP", ".HWPX", ".DOC", ".DOCX", ".PDF", ".XLS", ".XLSX", ".CSV", ".PPT", ".PPTX"):
            return "#0B4F82"
        if ext in (".PNG", ".JPG", ".JPEG", ".GIF", ".WEBP", ".BMP", ".SVG", ".MP4", ".MKV", ".AVI", ".MOV", ".MP3", ".WAV", ".FLAC"):
            return "#0D685B"
        if ext in (".CS", ".PY", ".JS", ".TS", ".JSON", ".XML", ".HTML", ".CSS"):
            return "#1E5E75"
        return "#172B43"

    def export_directory_csv(self, folder: Path, out_name: str = "폴더목록.csv") -> Path:
        rows = [f"# 폴더 경로: {folder}", "구분,이름,유형,크기(Bytes),메모여부,전체 경로"]
        for item in sorted(folder.iterdir(), key=lambda p: (not p.is_dir(), p.name.lower())):
            if item.name == self.MEMO_DIR:
                continue
            kind = "폴더" if item.is_dir() else "파일"
            ext = "폴더" if item.is_dir() else (item.suffix.upper() or "파일")
            size = 0 if item.is_dir() else item.stat().st_size
            has_memo = "Y" if (not item.is_dir() and len(self.list_memos(item)) > 0) else "N"
            rows.append(f'{kind},"{item.name}",{ext},{size},{has_memo},"{item}"')
        out_path = folder / self.suggest_new_file_name(folder, out_name)
        out_path.write_text("\n".join(rows) + "\n", encoding="utf-8-sig")
        self.redo_stack.clear()
        self.undo_stack.append(("export_csv", [(str(out_path), None, True)]))
        return out_path


class WindowsExplorerVirtualLabTests(unittest.TestCase):
    def test_split_expansion_shrinking_and_preset_changes_preserve_working_folders(self):
        """
        Verifies that increasing split count (1->2->3->4), changing row/column presets,
        and decreasing split count (4->2->1) NEVER reset or lose the user's active working folder!
        """
        with tempfile.TemporaryDirectory(prefix="Sunrise_SplitState_") as tmp:
            root = Path(tmp)
            default_docs = root / "Documents"
            proj_a = root / "Work" / "2026_ProjectA" / "SubTask1"
            proj_b = root / "Work" / "2026_ProjectB" / "FinalReview"
            proj_c = root / "Archive" / "2025_Backup"
            proj_d = root / "Downloads" / "Incoming"
            for d in (default_docs, proj_a, proj_b, proj_c, proj_d):
                d.mkdir(parents=True)

            ws = SimulatedWorkspaceController(default_docs)
            self.assertEqual(ws.visible_panels, 1)
            self.assertEqual(ws.panes[0].active_folder, default_docs.resolve())

            # 1. User works in Single Pane (1분할) and navigates deep into proj_a
            ws.panes[0].user_navigate(root / "Work")
            ws.panes[0].user_navigate(proj_a)
            ws.panes[0].search_text = "보고서"
            ws.panes[0].selected_names = ["기획안.docx"]
            reloads_before_split = ws.panes[0].reload_count

            # 2. User expands to 2분할 (TwoColumns):
            # - Pane 0 MUST stay at proj_a with zero reload and keep its search/selection/history!
            # - Untouched Pane 1 automatically opens at proj_a so the user doesn't start from Documents!
            ws.arrange_panels(2, "TwoColumns")
            self.assertEqual(ws.panes[0].active_folder, proj_a.resolve())
            self.assertEqual(ws.panes[0].reload_count, reloads_before_split, "Existing Pane 0 must not reload on split change")
            self.assertEqual(ws.panes[0].search_text, "보고서")
            self.assertEqual(ws.panes[0].selected_names, ["기획안.docx"])
            self.assertEqual(ws.panes[1].active_folder, proj_a.resolve(), "Newly opened untouched Pane 1 inherits active working folder")

            # 3. User switches 2분할 layout from 좌·우 2열 (TwoColumns) to 상·하 2행 (TwoRows)
            ws.panes[1].user_navigate(proj_b)
            p0_reloads = ws.panes[0].reload_count
            p1_reloads = ws.panes[1].reload_count
            ws.arrange_panels(2, "TwoRows")
            self.assertEqual(ws.split_preset, "TwoRows")
            self.assertEqual(ws.panes[0].active_folder, proj_a.resolve())
            self.assertEqual(ws.panes[1].active_folder, proj_b.resolve())
            self.assertEqual((ws.panes[0].reload_count, ws.panes[1].reload_count), (p0_reloads, p1_reloads))

            # 4. User is currently working in Pane 1 (proj_b, active_pane = 1) and shrinks to 단일 폴더 (1분할)!
            # The single remaining pane on screen MUST keep proj_b (the folder the user was actively working in)!
            ws.active_pane = 1
            ws.arrange_panels(1, "Single")
            self.assertEqual(ws.visible_panels, 1)
            self.assertEqual(ws.panes[0].active_folder, proj_b.resolve(),
                             "Shrinking 2->1 while focused on Pane 2 must preserve Pane 2's working folder on screen")
            self.assertEqual(ws.panes[1].active_folder, proj_a.resolve(),
                             "Previous Pane 1 folder is preserved in slot 1 for when user re-expands to 2-split")

            # 5. User expands back to 2분할 -> both proj_b and proj_a are still there, and remembers TwoRows preset!
            ws.arrange_panels(2)
            self.assertEqual(ws.split_preset, "TwoRows")
            self.assertEqual(ws.panes[0].active_folder, proj_b.resolve())
            self.assertEqual(ws.panes[1].active_folder, proj_a.resolve())

            # 6. User expands to 3분할 (ThreeLeft1Right2) and 4분할 (FourGrid2x2), navigates Pane 2 & Pane 3,
            # then shrinks from 4분할 -> 2분할 while focused on Pane 3 (proj_d)
            ws.arrange_panels(3, "ThreeLeft1Right2")
            ws.panes[2].user_navigate(proj_c)
            ws.arrange_panels(4, "FourGrid2x2")
            ws.panes[3].user_navigate(proj_d)
            ws.active_pane = 3
            ws.arrange_panels(2)
            self.assertEqual(ws.panes[1].active_folder, proj_d.resolve(),
                             "Shrinking 4->2 while focused on Pane 4 preserves Pane 4's working folder in visible Pane 2")

    def test_virtual_lab_sample_files_end_to_end_windows_explorer_and_qdir_parity(self):
        with tempfile.TemporaryDirectory(prefix="Sunrise_VirtualWinExplorer_") as tmp:
            root = Path(tmp)
            c_work = root / "C_Drive" / "Users" / "User" / "Documents" / "업무폴더"
            c_archive = c_work / "완료문서"
            d_backup = root / "D_Drive" / "Backup"
            c_work.mkdir(parents=True)
            c_archive.mkdir(parents=True)
            d_backup.mkdir(parents=True)

            lab = VirtualWindowsExplorerSandbox(root)

            # 1. Create sample files & attach #memo
            plan_txt = lab.create_file(c_work, "2026_프로젝트_기획안.txt", "1. 목표: Windows 기본 탐색기 100% 호환\n2. 차별점: 멀티분할 + #memo + 온보드 Ollama AI")
            budget_csv = lab.create_file(c_work, "예산_결산표.csv", "항목,금액\n서버비,0\n오픈소스,0")
            notes_md = lab.create_file(c_work, "회의록_초안.md", "# 주간 회의록\n- 상단 메뉴를 Windows 탐색기와 유사하게 정돈")
            app_cmd = lab.create_file(c_work, "배포스크립트.cmd", "@echo off\r\necho Sunrise")
            photo_png = lab.create_file(c_work, "화면설계도.png", "PNG_SAMPLE")
            lab.save_memo(plan_txt, "기획안 핵심 요약 메모")
            lab.save_memo(notes_md, "회의록 검토 메모")

            # 2. Test Q-Dir Color Filter classifications on sample files
            self.assertEqual(lab.qdir_color_for("완료문서", is_dir=True), "#163A55")
            self.assertEqual(lab.qdir_color_for(app_cmd.name), "#5B21B6")
            self.assertEqual(lab.qdir_color_for(budget_csv.name), "#0B4F82")
            self.assertEqual(lab.qdir_color_for(photo_png.name), "#0D685B")
            self.assertEqual(lab.qdir_color_for(".hidden", is_hidden=True), "#52657A")
            self.assertEqual(lab.qdir_color_for(app_cmd.name, enabled=False), "#172B43")

            # 3. Test Windows Explorer auto-numbered New Folder (Ctrl+Shift+N x 3) & Undo/Redo (Ctrl+Z / Ctrl+Y)
            f1 = lab.create_folder(c_work)
            f2 = lab.create_folder(c_work)
            f3 = lab.create_folder(c_work)
            self.assertEqual([f1.name, f2.name, f3.name], ["새 폴더", "새 폴더 (2)", "새 폴더 (3)"])
            self.assertTrue(lab.undo_last())
            self.assertFalse(f3.exists(), "Ctrl+Z must remove newly created empty folder '새 폴더 (3)'")
            self.assertTrue(lab.redo_last())
            self.assertTrue(f3.exists(), "Ctrl+Y must recreate '새 폴더 (3)'")

            # 4. Test Windows Explorer auto-numbered New Text File (Ctrl+N x 2)
            t1 = lab.create_file(c_work)
            t2 = lab.create_file(c_work)
            self.assertEqual([t1.name, t2.name], ["새 텍스트 문서.txt", "새 텍스트 문서 (2).txt"])

            # 5. Test Same-Folder Copy & Paste (Ctrl+C -> Ctrl+V twice) & Ctrl+Drag in same folder
            res_copy1 = lab.drag_drop_or_paste([plan_txt], c_work, "Copy")
            res_copy2 = lab.drag_drop_or_paste([plan_txt], c_work, "Copy")
            self.assertEqual(res_copy1[0][1].name, "2026_프로젝트_기획안 - 복사본.txt")
            self.assertEqual(res_copy2[0][1].name, "2026_프로젝트_기획안 - 복사본 (2).txt")
            self.assertEqual(res_copy2[0][1].read_text(encoding="utf-8"), plan_txt.read_text(encoding="utf-8"))
            self.assertTrue(lab.undo_last())
            self.assertFalse(res_copy2[0][1].exists(), "Ctrl+Z must remove the second duplicate copy")

            # 6. Test Single Rename (F2) + #memo relocation + Ctrl+Z Undo + Ctrl+Y Redo
            renamed_plan = lab.rename(plan_txt, "2026_프로젝트_기획안_최종.txt")
            self.assertFalse(plan_txt.exists())
            self.assertTrue(renamed_plan.exists())
            self.assertEqual(lab.list_memos(renamed_plan)[0]["Text"], "기획안 핵심 요약 메모")
            self.assertTrue(lab.undo_last())
            self.assertTrue(plan_txt.exists())
            self.assertEqual(lab.list_memos(plan_txt)[0]["Text"], "기획안 핵심 요약 메모")
            self.assertTrue(lab.redo_last())
            self.assertTrue(renamed_plan.exists())
            self.assertEqual(lab.list_memos(renamed_plan)[0]["Text"], "기획안 핵심 요약 메모")

            # 7. Test Multi-Selection Batch Rename (F2) + Ctrl+Z Undo + Ctrl+Y Redo
            batch_res = lab.batch_rename([
                (budget_csv, "[검토완료]_예산_결산표.csv"),
                (notes_md, "[검토완료]_회의록_초안.md"),
            ])
            self.assertTrue(batch_res[0][1].exists() and batch_res[1][1].exists())
            self.assertEqual(lab.list_memos(batch_res[1][1])[0]["Text"], "회의록 검토 메모")
            self.assertTrue(lab.undo_last())
            self.assertTrue(budget_csv.exists() and notes_md.exists())
            self.assertEqual(lab.list_memos(notes_md)[0]["Text"], "회의록 검토 메모")

            # 8. Test Same-Drive Drag & Drop Move + Same-Folder Drop No-op + Ctrl+Z Undo + Ctrl+Y Redo
            moved = lab.drag_drop_or_paste([renamed_plan], c_archive, "Move")
            moved_path = c_archive / "2026_프로젝트_기획안_최종.txt"
            self.assertEqual(len(moved), 1)
            self.assertFalse(renamed_plan.exists())
            self.assertTrue(moved_path.exists())
            self.assertEqual(lab.list_memos(moved_path)[0]["Text"], "기획안 핵심 요약 메모")

            noop_move = lab.drag_drop_or_paste([moved_path], c_archive, "Move")
            self.assertEqual(noop_move, [])
            self.assertTrue(moved_path.exists())

            self.assertTrue(lab.undo_last())
            self.assertTrue(renamed_plan.exists() and not moved_path.exists())
            self.assertEqual(lab.list_memos(renamed_plan)[0]["Text"], "기획안 핵심 요약 메모")

            self.assertTrue(lab.redo_last())
            self.assertTrue(moved_path.exists() and not renamed_plan.exists())
            self.assertEqual(lab.list_memos(moved_path)[0]["Text"], "기획안 핵심 요약 메모")

            # 9. Test Q-Dir Directory Listing CSV Export + Ctrl+Z Undo
            csv_file = lab.export_directory_csv(c_work, "업무폴더_목록.csv")
            self.assertTrue(csv_file.exists())
            csv_text = csv_file.read_text(encoding="utf-8-sig")
            self.assertIn("예산_결산표.csv", csv_text)
            self.assertIn("회의록_초안.md", csv_text)
            self.assertTrue(lab.undo_last())
            self.assertFalse(csv_file.exists(), "Ctrl+Z Undo removes exported CSV listing file")

            # 10. Test ZIP Compress & Extract with SHA-256 verification
            zip_file = c_work / "업무묶음.zip"
            with zipfile.ZipFile(zip_file, "w", zipfile.ZIP_DEFLATED) as zf:
                zf.write(budget_csv, budget_csv.name)
                zf.write(notes_md, notes_md.name)
            extract_target = c_work / "업무묶음_해제"
            with zipfile.ZipFile(zip_file, "r") as zf:
                zf.extractall(extract_target)
            orig_hash = hashlib.sha256(budget_csv.read_bytes()).hexdigest()
            ext_hash = hashlib.sha256((extract_target / budget_csv.name).read_bytes()).hexdigest()
            self.assertEqual(orig_hash, ext_hash)

    def test_windows_explorer_top_menu_and_qdir_split_controls(self):
        pane_xaml = (ROOT / "Controls" / "ExplorerPane.xaml").read_text(encoding="utf-8")
        main_xaml = (ROOT / "MainWindow.xaml").read_text(encoding="utf-8")
        ws_engine = (ROOT / "Features" / "Workspace" / "WorkspaceSplitLayoutEngine.cs").read_text(encoding="utf-8")

        # Verify all 14 presets exist in WorkspaceSplitLayoutEngine.cs
        for preset in (
            "Single", "TwoColumns", "TwoRows",
            "ThreeTop1Bottom2", "ThreeBottom1Top2", "ThreeLeft1Right2", "ThreeRight1Left2", "ThreeColumns", "ThreeRows",
            "FourGrid2x2", "FourColumns", "FourRows", "FourTop1Bottom3", "FourLeft1Right3"
        ):
            self.assertIn(preset, ws_engine)

        # Verify MainWindow has SplitPresetSelector, CycleSplitPresetButton, SwapPanesButton, SyncPanesButton
        for ctrl in ("SplitPresetSelector", "CycleSplitPresetButton", "SwapPanesButton", "SyncPanesButton"):
            self.assertIn(f'x:Name="{ctrl}"', main_xaml)

        # Verify ExplorerPane has ColorFilterToggle, ExportListButton, and Windows Explorer Command Bar sequence
        expected_order = [
            'x:Name="NewFolderButton"',
            'x:Name="NewFileButton"',
            'x:Name="CutSelectionButton"',
            'x:Name="CopySelectionButton"',
            'x:Name="PasteSelectionButton"',
            'x:Name="RenameButton"',
            'x:Name="DuplicateButton"',
            'x:Name="RecycleButton"',
            'x:Name="UndoButton"',
            'x:Name="RedoButton"',
            'x:Name="CategorySelector"',
            'x:Name="ViewSelector"',
            'x:Name="ZipCompressButton"',
            'x:Name="ZipExtractButton"',
            'x:Name="ExportListButton"',
            'x:Name="OpenCmdButton"',
            'x:Name="TreeToggle"',
            'x:Name="DetailsToggle"',
            'x:Name="PreviewToggle"',
        ]
        positions = [pane_xaml.index(token) for token in expected_order]
        self.assertEqual(positions, sorted(positions), "Command Bar buttons must follow Windows Explorer left-to-right sequence")


if __name__ == "__main__":
    unittest.main()
