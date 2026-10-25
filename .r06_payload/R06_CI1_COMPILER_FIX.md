# R06 Windows CI1: compiler-only fix candidate

This is an isolated validation branch. The original v1.7.0 R06 ZIP is preserved outside this branch.
The source payload omits old screenshot evidence PNGs but retains all build and Python test source.
The product revision remains V170_R06_LIGHT_DARK_SHELL_BUTTON_FINISH.

After first true Windows -warnaserror build, this candidate changes only:
- RecycleBinNativeDelete.cs: explicitly validate resolved Shell parent before call
- OfficialAShellAudit.cs: mark already-checked flyout as non-null
- SplitLayoutGalleryAudit.cs: mark already-checked context menu as non-null
- R06 Windows verification kit: equivalent flyout nullability annotation

Full R06 source original SHA256: 3a100ea86c9e9a9bbac2e8190ad92a8b9107845fb669faddad2f76e13a78a11b
CI1 compact source SHA256: b1603185e9ad122088e1393af12bca032d600b0f126260591bacd322497f04ed
CI1 kit SHA256: 27153d8235c22eac3255ec0b6636b65a17267e0ed315392ff6522b23ca415c39
Do not merge or release this branch without full Windows UI and distribution gates.
