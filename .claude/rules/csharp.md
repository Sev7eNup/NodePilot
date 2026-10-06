---
paths:
  - "src/**/*.cs"
  - "tests/**/*.cs"
---

# C# / .NET

Gilt zusätzlich zur Root-`CLAUDE.md`. Was Compiler und Analyzer schon erzwingen, steht hier nicht.

- Async durchgehend. Kein `.Result`, kein `.Wait()`, kein `GetAwaiter().GetResult()` auf I/O.
- `CancellationToken` durchreichen, wo die Operation sinnvoll abbrechbar ist. **Ausnahme:** Zustands- und Lifecycle-Writes, die auch bei Abbruch landen müssen, speichern mit `CancellationToken.None` (ein nach dem Commit gecanceltes `SaveChangesAsync` lässt Entities `Added` → doppelter INSERT; Guard: `StepStatePersistenceInvariantTests`).
- Nullability-Warnungen beheben, nicht mit `!` oder `#pragma` stummschalten.
- Ein Interface nur bei echter zweiter Implementierung oder echter Test-Naht (Remote-Layer, Uhr, LLM-Client). Nicht pro Klasse.
- Parallelität nur begrenzt (Semaphore/Channel mit Kapazität) und mit definiertem Fehler- und Cancel-Verhalten.
