# Incident Training Evidence Inventory

This read-only utility inventories the preserved incident training archive. It
separates raw development observations, direct human relationship reviews, and
withheld evaluation material. It deduplicates reviewed call pairs by their
application-owned source identities and verifies that their audio files exist.

The utility deliberately does not open any archive entry under
`heldout-sealed`. Model outputs and repeated experiment reports are counted as
archive files, not as independent human labels.

```powershell
dotnet run --project utilities\IncidentTrainingEvidenceInventory -- `
  C:\projects\pizzawave-incident-training-evidence-20260727.tar.gz `
  33F1CD7C892482F53C691D518A57BA7706638B2E21BE4DFB04EE0B6DB6CBFFFB
```
