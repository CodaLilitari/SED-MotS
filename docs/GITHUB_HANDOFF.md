# GitHub hosting and source-preserving migration

## Public source location — September 13, 2026

- Public repository: [CodaLilitari/SED-MotS](https://github.com/CodaLilitari/SED-MotS).
- Publishing/default branch: `main`.
- GitHub fork parent: [davidagnome/SED](https://github.com/davidagnome/SED).
- Original fork-network source: [smlu/SED](https://github.com/smlu/SED).
- Imported source checkpoint: `2a1461aea49f56ef834282b89d67a899bc5f2838`.

The public copy preserves the source commits, upstream ancestry, licensing and
original author credits. Andrei's repository remains a private preservation
copy. Binary releases for the Coda repository are pending; the build and
packaging instructions are in the [README](../README.md).

Local checkout remotes are separate from repository hosting. Inspect each
checkout's actual remotes and branch before publishing; this document does not
assert that an existing working checkout has been reconfigured.

## Historical initial private-hosting record — September 9, 2026

The sections below preserve the initial handoff. Their present-tense status,
local branch details and proposed later procedure refer to September 9, not
the current public location above. The recorded HTTP 403 remains an Andrei
account event; it is not evidence about the Coda account.

### Source relationship at initial hosting

- Hosting repository: [andreipemberton/SED-MotS](https://github.com/andreipemberton/SED-MotS).
- Visibility: private. GitHub repository type: standalone (`fork: false`).
- Development/default branch: `codex/mots-dotnet`.
- Immediate upstream: [davidagnome/SED](https://github.com/davidagnome/SED), whose GitHub parent/source network is [smlu/SED](https://github.com/smlu/SED).
- Audited .NET baseline: `908d53d70d6b95784a92e9f1746ab5b13afcfdf0`.
- Latest runtime-code checkpoint at initial hosting: `85fa15e9cfdcd25751e051a216ccf0d455a4064e`, Windows package `0.4.0-mots.2-dev`. The hosting documentation commit follows it without changing runtime code.

On 2026-09-09, the authenticated account could read the source repository but `POST /repos/davidagnome/SED/forks` returned HTTP 403 with `You cannot fork this repository at this time`. The user explicitly authorized a private standalone repository as the fallback. `POST /user/repos` succeeded with private visibility. That response establishes the present hosting state; it does not identify the cause of the account's fork restriction or establish that the restriction has been repaired.

The editor branch retains its original Git parents and upstream history. It is a source-derived GPLv3 fork in the ordinary code-provenance sense, while GitHub does not yet display it as a member of the upstream fork network. No squash, source reconstruction, or replacement initial commit was used. GitHub documents standalone mirrors separately from [fork relationships](https://docs.github.com/en/pull-requests/reference/forks); see also [repository duplication](https://docs.github.com/en/repositories/creating-and-managing-repositories/duplicating-a-repository).

### Local remotes and ongoing work at that handoff

The active checkout is on `codex/mots-dotnet`. `origin` points to the private repository and this branch tracks `origin/codex/mots-dotnet`. The existing `dotnet-source` and `upstream` remotes retain their read-only push configuration. Push the reviewed development branch normally after committing further work.

Only the development branch and its reachable history were uploaded for this handoff. Other local worktree branches and private research directories are outside this upload. The source tree includes its existing GPL license, dependency notices, tests and metadata audit reports. Game installations, downloaded community ZIPs, recovered proprietary tools/assets, ignored build outputs and local credentials remain external.

### Later-fork procedure proposed at that handoff

Once fork creation works for the account, create a real fork of `davidagnome/SED` under an available name. The private holding repository already occupies `SED-MotS`, so use a different temporary name or deliberately rename the holding repository while retaining it. Preserve this private backup until the new fork and all required commits are verified.

Then add the new fork as another remote and push `codex/mots-dotnet` to a branch of the same name. A normal branch push preserves every existing commit and its parent links; no history replay or squash is required. Fetch the upstream's then-current branches separately and merge or rebase deliberately if integration is wanted. Do not overwrite a newer upstream default branch merely to attach the project to its fork network.

Verify the new repository reports `fork: true` and the expected upstream network, and that its development branch SHA matches the reviewed local branch. Set the desired default branch after the source is present. Changing this standalone repository's description, visibility or remotes alone does not create a GitHub fork relationship.

No automatic retry, public visibility change, repository deletion or release publication is scheduled by these notes. The current repository remains the user's private working copy until that later handoff is performed.
