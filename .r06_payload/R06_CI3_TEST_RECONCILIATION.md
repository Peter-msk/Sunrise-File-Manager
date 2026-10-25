# R06 Windows CI3 test expectation reconciliation
Product code is unchanged from CI2.
- R06 workspace shell is Grid.Row=1 after removed transfer-toolbar row (old assertion Grid.Row=2 invalid).
- R06 3-line header totals 120 DIP, replacing old 100..118 assertion.
- Shared panel sync is tested with details, memo and preview actually opened; hidden panels are not obligated to load selection.
- Existing CI1/CI2 nullable compiler fixes remain.
Compact CI3 source SHA256: 896f9c6b2ccb847f7b91ba20e7d6ba1b96ba6c60b445de1c980f42d791912b6b
