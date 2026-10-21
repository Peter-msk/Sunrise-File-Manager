# Sunrise File Manager R19 R18 — Published v1.0.0 integration

## GitHub verified metadata (2026-10-21)
- Repository visibility: public; branch main
- Release: https://github.com/Peter-msk/Sunrise-File-Manager/releases/tag/v1.0.0 (release ID 419256009, published at 2026-10-21T07:11:15Z)
- Asset: Sunrise_File_Manager_1.0.0_Setup.exe (asset ID 661671264)
- GitHub-computed digest: sha256:eb866da915459cb1c61df22039957f6e07c9d863293a406a40ea5fcb47d62cdd
- Asset size: 51251864 bytes
- Product latest.json before fix had digest/size but empty URLs and update_endpoint_configured=false.
- Product notice.txt existed but said installer verification remained pending. Actual GitHub release is published; PC installation is not confirmed.

## R18 changes
1. Live `latest.json` activated for verified v1.0.0 Release, updated notice and canonical release body.
2. Runtime checks `update_endpoint_configured` and exact release tag + asset URL before downloads. SHA-256 and size verification preserved; installer needs user confirmation.
3. Future build generator writes **dist/release-metadata/** locally and never overwrites committed product feed files.
4. `release/PUBLISHED_RELEASE.json` binds the known asset hash+size; only matching local binaries may inherit current published download URL. If rebuilt EXE differs, generator disables its local download field to avoid same-version update corruption.
5. Release tag and installer EXE were neither replaced nor republished.

## Verification scope
- Original GitHub API release asset digest/size and repo visibility confirmed.
- Source/contract checks verify URL consistency, public file parsing and build safety. Live unauthenticated PC GUI/HTTP, Authenticode and installation were not run in this environment.
- Central Sunrise Control policy and product-specific update/notice remain independent.
