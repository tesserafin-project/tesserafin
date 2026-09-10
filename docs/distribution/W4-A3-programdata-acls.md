# W4-A3 (#234): breaking inheritance and granting `NT SERVICE\Tesserafin`

W4-A0 built an MSI that installs the accepted `win-x64` layout and registers the
service `Tesserafin`. W4-A1 froze the `UpgradeCode`. W4-A2 gave the installed
service the `docs/distribution/W0-windows-server.md` §4 recovery policy. None of
the three granted a single permission: each said so in as many words, and each
left the §9.3 ACL contract to a later slice. This is that slice, and it proves
exactly one thing:

> after the install, the live ACLs of the installed layout match W0 §9.3 — read
> with `Get-Acl` off the machine, by SID and by access mask, never out of the
> `.wxs`.

**Design this implements:** `docs/distribution/W0-windows-server.md` §9.2 and
§9.3.

---

## 1. The contract

W0 §9.3 states it as a table:

| Path | Grant |
| --- | --- |
| `%ProgramFiles%\Tesserafin\Server\` | `NT SERVICE\Tesserafin`: **read and execute only**. The service must not be able to rewrite its own binaries or its own FFmpeg |
| `%ProgramData%\Tesserafin\Server\config` | `NT SERVICE\Tesserafin`: Modify |
| `…\data`, `…\cache`, `…\log` | `NT SERVICE\Tesserafin`: Modify |
| all of the above | Administrators and SYSTEM: Full. `Users`: no inherited write |

> Inheritance is broken at `%ProgramData%\Tesserafin\` so a permissive parent ACL
> cannot silently widen access to the database.

**These grants are not what makes the service start, and this slice does not
start it to prove them.** W0 §9.2 already measured that: the service first
refused to start, the obvious explanation was that a virtual service account
inherits no execute right from `%ProgramFiles%`, that explanation was tested
directly by granting read and execute, and **it still failed**. The deciding
factor was where the process writes, not what it was granted, and W0 records
`aclGrantIsWhatMattered` as `false`. The default `%ProgramData%` ACL is already
permissive enough for the service to run. That is precisely the problem §9.3
exists to fix — it is permissive because it lets any authenticated user create
files there, which is not a property a directory holding the database should
have — and it is why a service that starts is not evidence about any of these
grants. W0 §10 leaves a fresh installation stopped; so does this run.

---

## 2. The identity, as a SID

`NT SERVICE\Tesserafin` is a virtual service account. SDDL takes SIDs, not
names, and this one is not arbitrary: the SID of `NT SERVICE\<name>` is
`S-1-5-80` followed by the SHA-1 of the **upper-case UTF-16LE** service name
read as five little-endian DWORDs. For `Tesserafin` that is

```
S-1-5-80-761762137-1691453069-3789821951-3290391601-3361247659
```

It is the same value on every machine, it can be computed before the service
exists, and `ci/windows/w4/msi-controls.py` **recomputes it from the service
name** rather than comparing it against a copy. That gate is not ceremony: a
well-formed SDDL naming some other SID installs perfectly, raises nothing, and
grants nobody anything. The same file validates its own implementation of the
derivation against the published SID of `MSSQLSERVER`.

---

## 3. The mechanism, settled by measurement

The W4-A3 ruling asks for `util:PermissionEx` "if Util 6.0.2 already covers it",
and forbids inventing a second extension. `WixToolset.Util.wixext` 6.0.2 is
already a dependency — W4-A2-R1 took it for `util:ServiceConfig` — so the
question was asked of the compiler rather than of the documentation:

```
$ wix build -ext WixToolset.Util.wixext/6.0.2 probe.wxs
error WIX0004: The PermissionEx element contains an unexpected attribute 'Sddl'.
error WIX0010: The PermissionEx/@User attribute was not found; it is required.
error WIX0004: The PermissionEx element contains an unexpected attribute 'Protected'.
error WIX0004: The PermissionEx element contains an unexpected attribute 'DenyInheritance'.
error WIX0004: The PermissionEx element contains an unexpected attribute 'NoInheritance'.
error WIX0004: The PermissionEx element contains an unexpected attribute 'ReplaceExisting'.
```

`util:PermissionEx` takes a user and a set of rights and can **add an ACE**. It
has no `Sddl` attribute and no attribute of any name that expresses protection,
so it **cannot break inheritance** — which is the one thing W0 §9.3 asks for by
name. It does not cover the contract, so the ruling's preference does not bind.

What is used instead is the **core `PermissionEx` element**, which writes the
Windows Installer 5.0 `MsiLockPermissionsEx` table. It is part of WiX itself and
needs no extension at all, so **no second extension is taken** and the
`WixToolset.Util.wixext` 6.0.2 pin W4-A2 established is unchanged.

The core **`Permission`** element is deliberately *not* used, and
`msi-controls.py` reddens its appearance. It writes the *other* table,
`LockPermissions`; Windows Installer refuses a package carrying both; and
`LockPermissions` always discards inherited permissions, which would take the
choice this slice is about away from the authoring and make the INSTALLFOLDER
grant in §4.2 impossible to express.

### 3.1 Hexadecimal masks, never the generic aliases

Every right in the authored SDDL is a file-specific hexadecimal access mask:

| Mask | Right |
| --- | --- |
| `0x1f01ff` | Full control |
| `0x1301bf` | Modify |
| `0x1200a9` | Read and execute |

The hosted proof reads them back through `Get-Acl`, which reports
`FileSystemRights` as exactly this mask. An SDDL generic alias (`GA`, `GR`,
`GX`) would come back as a raw number that no predicate could compare against a
named right, so a generic alias in either descriptor is a finding.

---

## 4. What the package authors

### 4.1 `%ProgramData%\Tesserafin\` — protected

```
D:P(A;OICI;0x1f01ff;;;BA)(A;OICI;0x1f01ff;;;SY)(A;OICI;0x1301bf;;;S-1-5-80-761762137-1691453069-3789821951-3290391601-3361247659)
```

`D:P` is the slice. `P` is `SE_DACL_PROTECTED`: the directory stops inheriting,
so the permissive `%ProgramData%` ACL that lets any authenticated user create
files cannot reach the tree that holds the database.

Three ACEs, and no fourth. There is no `Users`, `Authenticated Users` or
`Everyone` ACE in it at all, which is a stronger statement than the "no
inherited write" §9.3 asks for and is what makes a protected descriptor worth
having. Every ACE is `OICI`, so `Server\config`, `Server\data`, `Server\cache`
and `Server\log` — the four directories W0 §9.1 names — inherit `Modify` for the
service account and `Full` for Administrators and SYSTEM without each being
authored separately.

It hangs off a component in `TesserafinProgramData` that is deliberately **not**
`Permanent` and **not** `NeverOverwrite`, unlike the four retained-state
components beside it. The difference is what each owns. Those four own operator
*data*, which an ordinary uninstall must keep. This one owns a *security
descriptor*, which is package policy and has to be re-applied by every install
and every repair — and a `NeverOverwrite` component whose key path already
existed would simply be **skipped**, taking its `CreateFolder` and therefore its
descriptor with it. On uninstall the registry value goes, the directory removal
then fails because the four `Permanent` components keep the tree from being
empty, and the directory and its descriptor both survive: the behaviour W0 §10
asks for.

### 4.2 INSTALLFOLDER — read and execute, not protected

```
D:(A;OICI;0x1200a9;;;S-1-5-80-761762137-1691453069-3789821951-3290391601-3361247659)
```

One ACE, and no write bit in it. W0 §9.3's reason is stated in the table itself:
the service must not be able to rewrite its own binaries or its own FFmpeg.

It is deliberately **unprotected**. W0 §9.3 breaks inheritance at
`%ProgramData%\Tesserafin\` and **nowhere else**, and a protected descriptor here
would replace whatever the parent grants Administrators, SYSTEM and `Users` with
whatever this file happened to say. `MsiLockPermissionsEx` applied without `P`
adds the ACE and leaves inheritance alone.

It hangs off a `CreateFolder`, not off the `File` element beside it, because the
contract is about the **directory**: the payload is thousands of harvested files
in components of their own, and an ACE on one file would say nothing about the
tree beside it. Windows Installer applies `MsiLockPermissionsEx` during
`CreateFolders`, which the standard sequence runs before `InstallFiles`, so the
delivered tree inherits the `OICI` ACE as it is written rather than being fixed
up afterwards.

---

## 5. What is measured, and where

`ci/windows/w4/probe-msi-skeleton.ps1` reads the ACLs **after the install and
before the uninstall**, with `Get-Acl`, taking every rule as a
`SecurityIdentifier` rather than as an `NTAccount` — the descriptor carries a
SID, and a name translation is one more thing that can quietly fail and leave a
row unattributable. `AreAccessRulesProtected` is the managed name for
`SE_DACL_PROTECTED` and is the one boolean this slice is about.

**`DATAFOLDER` is NOT redirected.** Only `INSTALLFOLDER` is, to the disposable
prefix the W4-A0 ruling asked for. Every ACL graded under `%ProgramData%` is
therefore graded on the real `%ProgramData%\Tesserafin` tree an operator would
get, and the §4 argument list that is measured is the one an operator gets.

The consequence for `INSTALLFOLDER` is stated rather than hidden. Under a
disposable prefix its Administrators, SYSTEM and `Users` rows are inherited from
the runner's temp tree and **not** from `%ProgramFiles%`, so they are **recorded
as evidence and not graded** — grading them would grade `RUNNER_TEMP`. What *is*
graded there is the one row the package itself authors: the service account's.

### 5.1 The predicates

| Predicate | What it asks of the live ACL |
| --- | --- |
| `installFolderServiceCanReadAndExecute` | the service account's allow mask covers `0x1200a9` |
| `installFolderServiceCannotWrite` | it carries no bit that would let the holder change anything |
| `dataRootInheritanceBroken` | `%ProgramData%\Tesserafin` reports `AreAccessRulesProtected` |
| `stateDirectoriesServiceHasModify` | all four of `config`, `data`, `cache`, `log` grant the service account `0x1301bf` |
| `stateDirectoriesAdministratorsHaveFull` | all four grant `S-1-5-32-544` full control |
| `stateDirectoriesSystemHasFull` | all four grant `S-1-5-18` full control |
| `stateDirectoriesUsersHaveNoWrite` | none of the four grants `Users`, `Authenticated Users`, `Everyone` or `Guests` any write bit |

A directory the probe could not read grades every predicate about it **false**,
including the negative ones: "the install never created it" and "it exists and
grants nobody anything" are different findings and must not collapse into one.

`installFolderServiceCanReadAndExecute` and `installFolderServiceCannotWrite`
are two predicates and not one because `Modify` satisfies "read and execute". A
single predicate would be green for a package that handed the service account
the write access §9.3 exists to refuse.

---

## 6. The hostile controls

Four, each a deliberately broken package built from the **same** authoring
through the **same** grader, and each required to redden **exactly** the set it
declared.

| Mutation | What it authors | Declared RED |
| --- | --- | --- |
| `acl-not-protected` | `D:` instead of `D:P`, plus an explicit `Users` Modify ACE | `dataRootInheritanceBroken`, `stateDirectoriesUsersHaveNoWrite` |
| `acl-users-write` | `D:P` kept, `Users` handed Modify anyway | `stateDirectoriesUsersHaveNoWrite` |
| `acl-no-service-grant` | the protected descriptor grants the service account nothing | `stateDirectoriesServiceHasModify` |
| `acl-install-writable` | `0x1301bf` instead of `0x1200a9` on INSTALLFOLDER | `installFolderServiceCannotWrite` |

Three points about that table.

**`acl-not-protected` declares two, and the second is not collateral.** An
unprotected descriptor at `%ProgramData%\Tesserafin\` is only a defect *because*
the parent it then keeps inheriting from lets any authenticated user create
files there. The authoring reproduces that `Users` grant **explicitly** rather
than relying on the exact rows a given Windows build puts on `%ProgramData%`, so
the control produces the same two predicates on any host: one defect, two
visible consequences, declared in full the way `no-exe` declares three.

**`acl-install-writable` reddens the write half alone.** Read and execute still
hold under `Modify`, which is exactly why the two INSTALLFOLDER predicates are
separate.

**Every variant, mutants included, still grants Administrators and SYSTEM
Full.** None of these controls is about the administrative rights, and a mutant
that also dropped them would redden predicates it never declared and be
attributable to nothing. `msi-controls.py` asserts that invariant over every
authored variant, not just the real one.

Two further controls the ruling names are already covered and are not
re-authored here. **"UpgradeCode bytes moved"** is a static control W4-A1
established, driven by `msi-controls.py --self-test` in four shapes including
the same digits upper-cased and braced. **"Service started by the install"** is
the existing `serviceNotStartedByInstall` predicate, answered on all twelve runs;
a mutation that started the service would either fail with `1920` and roll the
install back to `1603` — hiding every other outcome behind one identity problem,
which W0 §5.2 already measured — or run the server, which this slice is not
authorised to do.

---

## 7. Resetting between controls

Twelve packages install and uninstall in one run, and two pieces of state
deliberately outlive an uninstall. Both had to start being removed between
controls, and `Reset-InstalledState` in the probe does it.

**`%ProgramData%\Tesserafin`** is where the descriptor under test lives.
`SetNamedSecurityInfo` leaves the protection flag alone unless it is told
otherwise, so a directory left **protected** by one control would still be
protected when the next control installs a deliberately **unprotected**
descriptor — and that control would grade green while proving nothing. The four
state components are `Permanent` by design, so the uninstall deliberately will
not do this and the probe has to.

**`HKLM\SOFTWARE\Tesserafin`** is those components' key path. They are
`NeverOverwrite`: with the key still present the installer **skips** them, their
`CreateFolder` never runs, and the state directories the previous cleanup just
deleted are never recreated. Removing the tree without removing the key would
have made every control after the first fail on
`stateDirectoriesSurvivedUninstall` for a reason that has nothing to do with
ACLs.

The same reset runs **before** the first control as well, because a cancelled
earlier run leaves exactly the state that would make it unattributable. The
runner is disposable and an operator's machine is not, which is exactly why the
package keeps both and only this script removes them.

---

## 8. What this slice does not claim

* it does **not** start the service, and the grants are not what would make it
  start (§1);
* it applies no ACL to anything outside `%ProgramData%\Tesserafin\` and
  `INSTALLFOLDER`;
* it makes **no** reproducibility claim. W0 §5.6 measured that MSI bytes are not
  bit-for-bit and accepted a bounded exception;
* it signs nothing, publishes nothing, uploads no artifact and creates no tag;
* the `UpgradeCode` `0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05` and the §4
  failure-action authoring are byte-identical to the base commit;
* it takes **no** new WiX extension: the `WixToolset.Util.wixext` 6.0.2 pin is
  W4-A2's and is unchanged, and the core `PermissionEx` element needs none;
* it exercises no upgrade, repair or Add/Remove Programs path beyond what
  `MajorUpgrade` emits by default;
* it changes no `Tesserafin.Server` behaviour and edits none of the frozen
  W1/W2/W3 scripts it runs;
* it makes no hardware-acceleration claim.

---

## 9. The residual uncertainty, stated

Whether Windows Installer honours the `P` in an `MsiLockPermissionsEx` SDDL —
that is, whether it passes `PROTECTED_DACL_SECURITY_INFORMATION` to
`SetNamedSecurityInfo` when the descriptor asks for it — is the one thing this
authoring cannot establish before a runner sees it. It is also the reason
`MsiLockPermissionsEx` is documented as preferable to `LockPermissions`, which
always discards inherited permissions and offers no choice.

So it is measured rather than asserted, and it is measured in a way that leaves
evidence either way: the probe **prints the full live ACL of all six directories
for every mutation, before anything is graded**. A run that refuses still leaves
the SID, the rights and the protection flag in the log, which is what the ruling
asks for.

---

## 10. Provenance

* Tracker: #234. Not closed by this slice.
* Ruling: **W4-A3 PROGRAMDATA ACLS** on #234.
* Base: `fc31d06a093e3c4f072a29be124b7c7edf1848d5` (W4-A2, accepted).
* Files: `packaging/windows/msi/Tesserafin.wxs`, `ci/windows/w4/**`, this
  document.
