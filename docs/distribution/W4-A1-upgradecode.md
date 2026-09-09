# W4-A1 (#234): the 1.1 MSI `UpgradeCode`, frozen

W4-A0 authored a `UpgradeCode` into `packaging/windows/msi/Tesserafin.wxs` and
was explicit that authoring it was not the same as ruling on it. This slice is
that ruling, carried into the tree and made mechanical. It proves exactly one
thing: that one string is pinned, and a build that carries any other value is
red before it reaches a runner.

## 1. The frozen value

    0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05

Ordinal, lowercase, no braces, exactly those bytes.

This is not a new GUID. It is the value W4-A0 already built with, present in the
authoring at the accepted W4-A0 master
`051699200423c0b055ba9599e62ab8041bdcfd3e`, and it was read back out of that
commit rather than re-minted — the owner ruling on #234 does not authorize a new
one. The bytes of the `UpgradeCode` attribute are unchanged by this slice; only
the prose around them moved.

## 2. Why this one value gets a ruling of its own

The `UpgradeCode` is the identity Windows Installer uses to decide that a new
package is an upgrade of an installed product rather than a second product
beside it. W0 §10 makes `MajorUpgrade` plus a stable `UpgradeCode` the mechanism
behind the deterministic in-place upgrade, and §5.6 already accepted that MSI
bytes are not reproducible — so the identity cannot be re-derived per build and
recovered later.

Every other value in the package can be corrected in a subsequent release. This
one cannot. Changing it after 1.1 ships strands every machine that already has
the product installed: the next installer no longer recognises the old product,
declines to upgrade it, and installs alongside it. That is why it is written
into the authoring rather than generated, and why it is now under a control
instead of under a comment asking a reviewer to notice it.

## 3. What "frozen" means here, precisely

It means the literal in the authored source. The comparison the control makes is
ordinal against a single string, deliberately not a GUID parse:

| Authored value | Verdict |
| --- | --- |
| `0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05` | the frozen value |
| `0F0C9F4E-1C5A-4B8E-9A3D-6D1F2B7C8E05` | RED — same digits, different case |
| `{0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05}` | RED — same digits, braced |
| any other GUID | RED |
| the attribute removed, or supplied through a preprocessor variable | RED |

A GUID parser would call the first three rows equal. The ruling does not, and
neither does the control: what is frozen is the source text a reviewer reads,
because that is the thing a later edit would change. Windows Installer's own
canonical upper-case braced rendering of the same value inside a built MSI is a
separate representation and is not what this freeze governs.

## 4. What is graded, and where

`ci/windows/w4/msi-controls.py` runs before any runner time is spent, and the
W4 workflow already invokes it with `--self-test`, so the controls below run
hosted and not only on an author's machine.

| Gate | Reads | Fires when |
| --- | --- | --- |
| `findings_for_upgrade_code` | the authoring with comments stripped | the authored attribute is not the frozen string, is absent, or is stated more than once |
| `findings_for_upgrade_code_prose` | the authoring's comments, this document, `W4-A0-wix-skeleton.md` | a comment or a document still describes the value as open, or the authoring's comments stop citing W4-A1 and the frozen string |

The prose gate exists because the ruling names it: *"A comment that claims it is
unfrozen is RED."* A pin that the surrounding prose contradicts is worse than no
pin, because the next reader believes the sentence rather than the attribute.
The attestation is required to live in a comment, so the attribute alone cannot
satisfy it, and the forbidden patterns are the W4-A0-era wording — a straight
revert of either document trips them.

`self_test_upgrade_code` mutates the authoring and both documents seven ways and
requires every mutation to be caught. A gate that cannot be made to fail has not
been shown to be a gate.

## 5. Observed RED

Each mutation below was written to disk, graded by
`python3 ci/windows/w4/msi-controls.py` with no arguments — the ordinary
grading path, not the self-test — and reverted with `git checkout --` from the
commit that carries this document. All six exited 1. The tree was clean
afterwards, and the controls were green again on the restored tree.

| Mutation | Exit | Finding |
| --- | --- | --- |
| `UpgradeCode` replaced with `6b1e8d37-5f92-4a04-8e7c-3d05b9f2a618` | 1 | `authoring: UpgradeCode is '6b1e8d37-…', but W4-A1 froze '0f0c9f4e-…' -- ordinal, lowercase, no braces` |
| same digits, upper case | 1 | `authoring: UpgradeCode is '0F0C9F4E-1C5A-4B8E-9A3D-6D1F2B7C8E05', but W4-A1 froze '0f0c9f4e-…'` |
| same digits, braced | 1 | `authoring: UpgradeCode is '{0f0c9f4e-…}', but W4-A1 froze '0f0c9f4e-…'` |
| the attribute removed | 1 | `authoring: no UpgradeCode attribute; W4-A1 froze one and the package must carry it` |
| the authoring's comment calls it unfrozen | 1 | `authoring comment: still says the UpgradeCode is open ('unfrozen'); W4-A1 froze it` |
| `W4-A0-wix-skeleton.md` reverted to "Nothing here freezes them" | 1 | `W4-A0 document: still says the UpgradeCode is open ('nothing\s+here\s+freezes'); W4-A1 froze it` |

The upper-case and braced rows are the ones that matter most: they are the two
a GUID-parsing gate would have accepted, and the ruling names them explicitly.

## 6. What this slice is not

* not the W0 §4 recovery actions (restart 60 s ×2);
* not the W0 §9 ACLs;
* not signing;
* not a bit-identical MSI;
* not starting the service;
* not a claim that W4 is accepted, or that W3 is.

It also rules on **nothing else in the MSI identity**. The package name
`Tesserafin Server`, the manufacturer `Tesserafin project` and the four
retained-state component GUIDs (`RetainedConfig`, `RetainedData`,
`RetainedCache`, `RetainedLog`) are still only the identity the W4-A0 skeleton
happened to build with. The component GUIDs are as load-bearing for the
retained-data policy as the `UpgradeCode` is for upgrade, and they each still
need an explicit decision before 1.1 ships. This slice does not take it.

`docs/distribution/W4-A0-wix-skeleton.md` §9 still lists the `UpgradeCode` among
the things a reviewer should check next. That sentence is W4-A0-era and is
answered by this document; the ruling authorized editing only the sentence that
said the value was not a claim, so §9 was left exactly as it stands rather than
widened into silently.

## 7. Evidence

**Base.** Branched from `051699200423c0b055ba9599e62ab8041bdcfd3e`, the
accepted W4-A0 master named by the ruling, confirmed equal to `origin/master`
before any file was touched.

**The value was recomputed, not restated.** The attribute was read out of the
base commit and dumped byte for byte: `UpgradeCode="0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05"`,
63 bytes, lowercase, unbraced. It matches the ruling, so nothing was "fixed".

**The `.wxs` change is comment-only.** Stripping `<!--…-->` (dot-matches-all)
from the base authoring and from this branch's authoring yields identical text,
so no element, attribute or attribute value moved — including the frozen one.
The file is well formed, and no XML comment contains a double hyphen.

**Baseline.** `msi-controls.py --self-test` was run on the untouched base
commit first and was clean with its five original controls. Without that, a
green run afterwards would prove nothing.

**After.** `msi-controls.py --self-test` is clean, with 5 original controls and
7 UpgradeCode freeze controls, all RED as declared.

**Changed paths.** Exactly the four the ruling authorizes:
`packaging/windows/msi/Tesserafin.wxs`, `ci/windows/w4/msi-controls.py`,
`docs/distribution/W4-A0-wix-skeleton.md` and this document.

**Secret scan.** `ci/secret-scan.sh --mode tree` — `CLEAN: the current tree
contains no findings`, exit 0, on a worktree with no build output present.
