# Decisions and spec conflicts

Precedence (doc 03): doc 02 wins on *how*, doc 01 wins on *what*.

| # | Topic | Conflict / choice | Resolution |
|---|---|---|---|
| 1 | Platform | Doc 02 suggests .NET 10 LTS, "9 is fine too". | .NET 9 (installed SDK). See ASSUMPTIONS. |
| 2 | Hosting of SPA | Doc 01 mentions Vercel in the original plan. | Same-origin: the API serves `wwwroot` (doc 02 D-11, P-01). |
| 3 | Search operator | Doc 02 §9 shows `ILIKE`. | `LIKE lower(body)` + trigram index on `lower(body)` — equivalent semantics, keeps Application provider-agnostic. |
| 4 | Status codes for register | Doc 02 §8 lists 202/422/429 for register. | Same, plus 409 for a taken username (doc 01 USR-02 uniqueness). |
| 5 | Previous scaffold | Doc 04 asks to refactor/rewrite deviations. | Rewritten from scratch (Minimal API → controllers, CQRS pipeline, new migration). Old files kept in `.scaffold-backup/`. |
| 6 | Git | Doc 03 says "commit per phase"; doc 04 (newer, overrides) says no git operations. | No commits were made; the team commits. |

No requirement from doc 01 was dropped.
