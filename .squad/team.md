# Squad Team

> azure-demos-ai-agent-monitoring-insights-actions

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Lead | Architecture, implementation and review | [Charter](agents/lead/charter.md) | Active |
| Backend | .NET, Cosmos and scheduling | [Charter](agents/backend/charter.md) | Active |
| Infra | Build, deployment and emulator | [Charter](agents/infra/charter.md) | Active |
| Tester | Tests and verification | [Charter](agents/tester/charter.md) | Active |
| Scribe | Decision and history maintenance | [Charter](agents/scribe/charter.md) | Built-in |
| Ralph | Work queue monitoring | [Charter](agents/ralph/charter.md) | Built-in |
| Rai | Responsible AI | [Charter](agents/Rai/charter.md) | Built-in |
| Fact Checker | Evidence verification | [Charter](agents/fact-checker/charter.md) | Built-in |


## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Project:** azure-demos-ai-agent-monitoring-insights-actions
- **Created:** 2026-10-19
