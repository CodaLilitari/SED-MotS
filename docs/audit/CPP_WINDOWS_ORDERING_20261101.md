# Windows native-core ordering qualification — 2026-11-01

The unchanged upstream core at
`b82255998fb733c8e8c87104eb7c17f27b48376f` compiled on both platforms. Linux
passed all 51 CTest registrations. Windows passed 50; its aggregate core suite
reported eight failures of the generated-crossing directed-order assertion in
`src/core/tests/level/extrude_arrangement_registry_tests.cpp` around line 530.
The baseline failure was preserved and no upstream source was patched.

## Discriminating experiment

Small probes linked the already-built unchanged core and reproduced the eight
endpoint-direction permutations. A separate Python `Fraction` oracle computed
exact intersection/order signs from the captured binary64 operands. Each
platform's captured operands were then replayed through both compiled exact
predicates, without rebuilding the full core.

| Observation | Windows LLVM-MinGW Clang 23.1.4 | Linux GCC 15.2 |
|---|---|---|
| Fixed fixture ordering mismatches | 8 of 8 | 0 of 8 |
| Normalization `axisLength` bits | `3fefffffffffba61` | `3fefffffffffba62` |
| `sizeof(long double)` | 16 | 16 |
| `long double` significand bits | 64 | 64 |
| `FLT_EVAL_METHOD` | 0 | 0 |
| Reported rounding mode | 0 | 0 |

The first recorded divergence is one ULP in the three-component `std::hypot`
result used to normalize the local frame. The normalized axes and projected
coordinates subsequently differ. In the first permutation, Windows returns
order `+1`; Linux returns `-1`, the fixture's fixed expectation.

The independent rational oracle agrees with the registry and mirrored
predicate result for all 16 platform-specific operand cases. All 32 executions
of the identical-bit replay agree across the two compilers and with the oracle.
Exact comparison against the midpoint of the adjacent axis-length candidates
also shows the Linux candidate is the nearer binary64 rounding for this input.

This establishes a frame/projection operand difference for the examined fixture.
It does not establish whether the `hypot` difference comes from library code,
builtin expansion or another lowering choice. No disassembly was performed,
and this single input is not a claim of C++ conformance failure. A differing
`long double` ABI does not explain this result.

## Disposition

The Windows full-core geometry gate remains failed. The arrangement path is
used by extrusion/cleave machinery and is not called by the strict native-Sed
or JKL inspection entry points exposed in the optional worker. The separately
passed inspection tests therefore retain their bounded scope; they do not
approve topology mutation or universal numerical parity.

Before changing upstream, choose the intended geometry contract. Identical 3D
inputs producing identical decisions across platforms would require a portable
frame/projection contract or predicates expressed against original operands.
Exactness over each platform's already-rounded local coordinates is a different
contract, under which this nearly degenerate fixture's fixed expectation is
not portable. A frozen-operand predicate fixture and better-separated registry
fixture would then be an explicit reviewed test correction. Neither conclusion
justifies quietly relaxing the current assertion.

The external task record retains the original failed baseline, both phase
receipts, captured operand bits, cross-platform replay output, rational oracle
and diagnosis. An optional aggregate file-manifest writer stalled during storage
contention and was cancelled; that missing artifact is not claimed as completed.
