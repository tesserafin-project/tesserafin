#!/usr/bin/env python3
"""The canonical `pkg_tree_digest`, on a runner with no GNU tar.

`ci/package/lib.sh` defines exactly one tree identity for this project:

    tar --create --directory <dir> --sort=name \
        --owner=0 --group=0 --numeric-owner \
        --mtime=@<epoch> --format=gnu --exclude-vcs .

hashed with sha256 over the resulting archive FILE. Every Linux package gate
speaks that vocabulary, and W2 must not invent a second one: the Windows ZIP has
to be able to say "this is the same web payload the .deb ships" and be believed.

Windows runners have no GNU tar, so this is the same definition re-expressed
against Python's `tarfile` in GNU format. That is not an approximation. The
archives are byte-identical, which is what `--check-oracle` asserts against a
real `tar(1)` whenever one exists, and what C19 in web-payload-controls.py
asserts against a digest recorded from GNU tar 1.35 on Linux.

Two deliberate differences from a bare `tar` invocation, both of which make this
a faithful expression of what `ci/package/assemble-payload.sh` actually hashes
rather than of what `tar` would do to an arbitrary directory:

  * Modes are normalised to 0755/0644 rather than read from disk.
    assemble-payload.sh normalises the extracted web tree that way immediately
    BEFORE calling pkg_tree_digest, precisely so the pinned digest survives a
    trip through any filesystem. Windows has no POSIX mode to read back, so
    reading one would be the approximation.

  * `--exclude-vcs` is not reimplemented. GNU tar's exclusion list is a moving
    target and silently dropping a file is the one failure mode a digest cannot
    report. Instead, any name on that list is a hard error: the accepted web
    payload contains none, and a payload that grows one must be reviewed rather
    than absorbed by two implementations that might disagree about it.

Owner/group are 0/0 numeric with empty user and group names, every mtime is
clamped to the epoch, entries are sorted by name, and the walk starts at ".",
exactly as the shell definition does.
"""

import argparse
import hashlib
import os
import subprocess
import sys
import tarfile
import tempfile

# GNU tar's --exclude-vcs set, as of tar 1.35. Presence is an error, never an
# exclusion: see the module docstring.
VCS_NAMES = frozenset([
    "CVS", ".cvsignore",
    "RCS", "SCCS",
    ".svn",
    ".git", ".gitignore", ".gitattributes", ".gitmodules",
    ".hg", ".hgignore", ".hgtags", ".hgsub", ".hgsubstate",
    ".bzr", ".bzrignore", ".bzrtags",
    "_darcs",
    "{arch}", ".arch-ids", "=RELEASE-ID", "=meta-update", "=update",
])

DIR_MODE = 0o755
FILE_MODE = 0o644


class TreeDigestError(Exception):
    """A tree that cannot be hashed under the canonical definition."""


def _sorted_names(directory):
    # Sorted by encoded name, so the ordering does not depend on the locale or
    # on the platform's idea of string collation.
    return sorted(os.listdir(directory), key=lambda n: n.encode("utf-8", "surrogateescape"))


def write_canonical_tar(root, epoch, dest):
    """Write the canonical deterministic tar of `root` to `dest`."""
    root = os.path.abspath(root)
    if not os.path.isdir(root):
        raise TreeDigestError("not a directory: %s" % root)

    archive = tarfile.open(dest, "w", format=tarfile.GNU_FORMAT)
    try:
        _add(archive, root, ".", int(epoch))
    finally:
        archive.close()


def _add(archive, abspath, relpath, epoch):
    if os.path.islink(abspath):
        raise TreeDigestError("symbolic link in a canonical tree: %s" % relpath)

    info = tarfile.TarInfo(relpath)
    info.mtime = epoch
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""

    if os.path.isdir(abspath):
        info.type = tarfile.DIRTYPE
        info.mode = DIR_MODE
        info.size = 0
        archive.addfile(info)
        prefix = "" if relpath == "." else relpath
        for name in _sorted_names(abspath):
            if name in VCS_NAMES:
                raise TreeDigestError(
                    "%s/%s is on GNU tar's --exclude-vcs list; the canonical digest "
                    "is only defined for trees that contain none" % (prefix or ".", name))
            child_rel = ("%s/%s" % (prefix, name)) if prefix else ("./%s" % name)
            _add(archive, os.path.join(abspath, name), child_rel, epoch)
        return

    if not os.path.isfile(abspath):
        raise TreeDigestError("not a regular file or directory: %s" % relpath)

    info.type = tarfile.REGTYPE
    info.mode = FILE_MODE
    info.size = os.path.getsize(abspath)
    with open(abspath, "rb") as handle:
        archive.addfile(info, handle)


def tree_digest(root, epoch):
    """sha256 of the canonical deterministic tar of `root`."""
    handle, path = tempfile.mkstemp(prefix="pkg-tree-digest-", suffix=".tar")
    os.close(handle)
    try:
        write_canonical_tar(root, int(epoch), path)
        digest = hashlib.sha256()
        with open(path, "rb") as archive:
            for chunk in iter(lambda: archive.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest()
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


def gnu_tar_digest(root, epoch):
    """The same digest computed by tar(1), or None where no GNU tar exists."""
    handle, path = tempfile.mkstemp(prefix="pkg-tree-digest-gnu-", suffix=".tar")
    os.close(handle)
    try:
        for candidate in ("tar", "gtar"):
            try:
                banner = subprocess.run(
                    [candidate, "--version"], stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL, check=False)
            except OSError:
                continue
            if banner.returncode != 0 or b"GNU tar" not in banner.stdout:
                continue
            result = subprocess.run([
                candidate, "--create", "--file", path, "--directory", root,
                "--sort=name", "--owner=0", "--group=0", "--numeric-owner",
                "--mtime=@%d" % int(epoch), "--format=gnu", "--exclude-vcs", ".",
            ], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=False)
            if result.returncode != 0:
                raise TreeDigestError(
                    "%s failed: %s" % (candidate, result.stderr.decode("utf-8", "replace").strip()))
            digest = hashlib.sha256()
            with open(path, "rb") as archive:
                for chunk in iter(lambda: archive.read(1024 * 1024), b""):
                    digest.update(chunk)
            return digest.hexdigest()
        return None
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("directory")
    parser.add_argument("epoch", type=int)
    parser.add_argument(
        "--check-oracle", action="store_true",
        help="also compute the digest with GNU tar, where one exists, and require agreement")
    args = parser.parse_args(argv)

    try:
        digest = tree_digest(args.directory, args.epoch)
    except TreeDigestError as error:
        sys.stderr.write("pkg-tree-digest: %s\n" % error)
        return 2

    if args.check_oracle:
        try:
            oracle = gnu_tar_digest(args.directory, args.epoch)
        except TreeDigestError as error:
            sys.stderr.write("pkg-tree-digest: %s\n" % error)
            return 2
        if oracle is None:
            sys.stderr.write("pkg-tree-digest: no GNU tar on this host; oracle not run\n")
        elif oracle != digest:
            sys.stderr.write(
                "pkg-tree-digest: GNU tar says %s, this implementation says %s\n" % (oracle, digest))
            return 2
        else:
            sys.stderr.write("pkg-tree-digest: GNU tar agrees\n")

    sys.stdout.write(digest + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
