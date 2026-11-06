# Machine reset recovery checkpoint

Snapshot: 2026-11-06. D3 is work in progress and is not qualified.

Original checkpoint: `de7ef71b05ebd1b549e349b97d1c916df2ca88ba`. Main is unchanged.

GitHub rejected the normal branch push because its OAuth credential lacks workflow
scope. This recovery branch therefore uses the already-published workflow directory
from `8a0c2aae2eafa43f4ac6c3ec8aee89451ac4a932`. All other tracked source files match
the checkpoint. The bundle preserves the original commits and workflow files,
52 branch/tag refs, including all category and step branches. It is an inert archive,
not an update to active GitHub workflows.

Bundle SHA-256: `2962f6bd33a26545b86c5cd5a1751a26695dd494d359477cd86361f42f6790eb`.

## Restore the original development history

From this recovery checkout, use fresh destination directories:

```sh
git bundle verify recovery/nealytics-all-branches.bundle
git clone --mirror recovery/nealytics-all-branches.bundle ../nealytics-history.git
git clone --branch feat/developer-operator-tools-d3-kafka-source-health ../nealytics-history.git ../nealytics-restored
git -C ../nealytics-restored remote set-url origin https://github.com/neavents/nealytics.git
```

The mirror retains every original branch/tag. The restored working checkout starts
on the exact D3 checkpoint. Read `docs/reset-handoff.md` before continuing. Normal
feature-branch pushes containing workflow changes still need workflow permission.
Do not merge into main.

Chat history, ignored evidence and original-checkout rebase state are private and
are not included in this public recovery branch or its source-history bundle.
Those require the separate private reset backup.
