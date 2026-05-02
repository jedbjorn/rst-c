# spike/

Throwaway proof-of-approach files. Excluded from the solution and from
all `dotnet build` runs (guarded by `#if SPIKE_BUILD`).

These exist to:

- Prove a porting strategy on paper before committing a project to it.
- Anchor `DECISIONS.md` entries to concrete code shapes.
- Serve as a reading list for the engineer landing the real port.

When the real implementation lands (e.g. `src/RST.Engine/Scanning/`), the
matching spike file is *deleted* — never edited in place.

| Spike                | Anchors flag | Real impl path                 |
|----------------------|--------------|--------------------------------|
| `ScannerSpike.cs`    | RST-003      | `src/RST.Engine/Scanning/`     |
