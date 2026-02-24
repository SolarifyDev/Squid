# Squid.Tentacle.Tests

Tentacle-focused test library modeled after the layered structure used in Octopus Tentacle tests.

## Intended layers

- `Core/`: generic abstractions, adapters, resolver/catalog behavior
- `Flavors/`: flavor runtime wiring (KubernetesAgent now, Linux/Windows later)
- `Support/Scenarios/`: reusable scenario matrix (similar to Octopus test case source)
- `Support/Lifecycle/`: reusable lifecycle harnesses for startup hooks/background tasks
- `Integration/`: process-level `Squid.Tentacle` startup smoke now, real Halibut/fault-injection suites next
- `Kubernetes/Integration` (future): Kind/Helm/Kubectl install + runtime fault scenarios

This project starts with core/generic smoke coverage so future tentacle flavors can plug into an existing test skeleton.

## Stability notes

- Process/network tests use a dedicated xUnit collection with parallelization disabled.
- Startup smoke tests use unique temp cert/work directories and dynamically allocated localhost ports.
- Repeated startup smoke runs are included to catch timing/cleanup regressions without relying on repeated `dotnet test` invocations.
