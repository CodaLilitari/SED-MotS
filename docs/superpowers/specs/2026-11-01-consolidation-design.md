# Linux consolidation and Windows parity

Andrei requested recovery of the outstanding SED-MotS work, integration of Red's
and Lily's separately maintained changes, a consolidated Linux branch followed
by Windows feature parity, and an evidence-based upstream/editor compatibility
review. A subsequent correction explicitly excludes contacting Red.

## Source identities

- Hosted main: `ff881828be1eea35999dda46f7f1f6d940e81b8f`.
- Native Linux: `419637c870ce674f347de1ab982e37068c4cd4ac`.
- Reviewed dependency packaging: `c9f3ca837f3ae5c30bcfd19e98cfd14d0cff4a1d`.
- Red's GitLab history: `0a99e4ac09cba7aab0bccfc88a28a2d0eca80d8d`.
- GitHub replay: `6fcc36c522eda0da9f55ce44d1c0c36dc8573050`.

The two Red tips have identical trees. Both contain the Linux and packaging
branches. A merge preserves both histories without replaying their changes.
Commit authorship and notices remain intact; a branch label is not proof that
every commit was authored by a particular collaborator.

## Approach

For the reversible consolidation checkpoint, use .NET 10 / Avalonia / Vulkan and one shared
Core/Formats implementation for both operating systems. Start
`codex/linux-consolidated-20261101` from Red's GitLab tip, merge the identical
GitHub replay history, apply bounded compatibility and packaging corrections,
then derive a Windows parity branch from the validated common implementation.

**Architecture decision reopened by Andrei:** obtain Lilitari's and other
authorized reviewers' assessment of selective ports, a C++ core integration,
and a fuller migration to upstream's rewrite. Red is excluded from contact.
The existing-stack consolidation is a preservation checkpoint, not a final
decision against broader C++ adoption. Compare exact current source and
discriminating fixtures before choosing the longer-term architecture.

Cherry-picking every replayed commit would duplicate changes already present.
Any migration to upstream's C++/Qt editor must explicitly preserve or replace
this fork's authoring and agent contracts. Compare selective transfer, a native
core boundary and a full platform migration against exact source and behavioral
evidence; current language choice and accumulated tests do not settle the outcome.

## Compatibility contract

Legacy spellings accepted by the existing reader must remain readable after
supported edits. Preserve retained parameter spelling and existing parameter
order; preserve diagnostics for malformed but recognized legacy inputs. Do not
silently repair arbitrary geometry, normalize unknown fields, or make resource
completeness claims from successful parsing. Regression fixtures are original;
community and retail levels remain external research inputs.

## Windows parity

The Windows distribution must carry the same shared features, support tools,
attribution and dependency-packaging workflow as Linux. Platform-specific Wine
capture remains a Linux tool and is documented as such. Build intermediates,
packages, caches and captures stay physically outside Git worktrees. Run Git
serially with real child deadlines. Preserve existing Windows checkout changes.

## Evidence

Run source tests, packaged CLI/MCP workflows and available graphics checks on
each target. Distinguish source integration, compiled output, package content,
runtime observations, historical collaborator receipts and new local receipts.
Upstream source availability does not establish a successful build. Browser
research depends on the authorized Chrome connection; no Discord posting is
part of this request.
