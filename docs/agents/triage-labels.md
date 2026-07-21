# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those roles to the label string each role uses in this repo's issue tracker (`Sev7eNup/NodePilot`).

| Role (mattpocock/skills) | Label string | In tracker? | Meaning |
| --- | --- | --- | --- |
| `needs-triage` | `needs-triage` | ✅ | Maintainer needs to evaluate this issue |
| `needs-info` | `needs-info` | ✅ | Waiting on reporter for more information |
| `ready-for-agent` | `ready-for-agent` | ✅ | Fully specified, ready for an AFK agent |
| `ready-for-human` | `ready-for-human` | ✅ | Requires human implementation |
| `wontfix` | `wontfix` | ✅ | Will not be actioned |

When a skill mentions a role, use the corresponding label string from this table. All five labels exist in the GitHub tracker (`needs-triage` + `needs-info` created 2026-06-23), so `gh issue edit --add-label <role>` works for every role.
