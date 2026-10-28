#!/usr/bin/env bash
# Installs the native packages the hosted test jobs need (ci-tests.yml): the
# same ffmpeg and libfontconfig1 that Dockerfile.ci installs for ./ci/run.sh.
#
# Downloaded archives live in ~/.cache/native-test-debs, which the workflow
# restores and saves with actions/cache, so a warm run downloads nothing. The
# package lists are always refreshed: the cache spares downloads, it never pins
# versions. Per-request timeouts keep one stalled archive from hanging apt for
# minutes; the workflow step's own timeout bounds the whole install.
set -euo pipefail

debs="$HOME/.cache/native-test-debs"
apt_opts=(-o Acquire::Retries=2 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 -o "Dir::Cache::archives=$debs")

sudo mkdir -p "$debs/partial"
echo "Cached archives: $(find "$debs" -maxdepth 1 -name '*.deb' | wc -l)"
sudo apt-get "${apt_opts[@]}" update
sudo apt-get "${apt_opts[@]}" install -y --no-install-recommends ffmpeg libfontconfig1

# Drop archives that are no longer downloadable, and leave a tree the
# unprivileged cache action can archive.
sudo apt-get "${apt_opts[@]}" autoclean
sudo rm -rf "$debs/partial" "$debs/lock"
sudo chown -R "$(id -u):$(id -g)" "$debs"
