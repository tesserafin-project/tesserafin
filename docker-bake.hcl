# Buildx bake definition for the reproducible Tesserafin production image (#87).
#
# One commit builds both linux/amd64 and linux/arm64.
#
# THIS FILE DERIVES NO TAGS AND NO VERSIONS (#92 / [A6]). Tag policy lives in
# exactly one place — docker/version-contract.sh — and reaches bake through the
# variables below. docker/build-clean.sh exports them; a workflow step exports
# them the same way. docker/version-contract.test.sh asserts that the tags bake
# prints are byte-identical to the tags the contract emits, so the two cannot
# drift apart silently.
#
# Reproducible builds require disabling provenance/SBOM attestations, which embed
# build timestamps: pass `--provenance=false --sbom=false` (docker/build-clean.sh
# does this).

# Canonical version, from SharedVersion.cs via docker/version-contract.sh.
# The default is an obviously invalid placeholder on purpose: an image built
# without the contract must be recognisable at a glance rather than plausible.
variable "VERSION" {
  default = "0.0.0-unset"
}

# Full 40-char commit SHA of the tree being built.
variable "VCS_REF" {
  default = "0000000000000000000000000000000000000000"
}

# Commit time as UNIX seconds — clamps file/layer timestamps for reproducibility.
variable "SOURCE_DATE_EPOCH" {
  default = "0"
}

# Commit time as RFC3339 — the OCI `created` label. Deterministic per commit.
variable "BUILD_DATE" {
  default = "1970-01-01T00:00:00Z"
}

# Comma-separated, fully-qualified image references produced by
# `docker/version-contract.sh env`. Empty is not a usable value: bake then emits
# one empty tag and docker refuses the build, which is the intended fail-closed
# behaviour for "somebody bypassed the contract".
variable "TAGS" {
  default = ""
}

# Default target: both supported architectures from the same source commit.
target "server" {
  context    = "."
  dockerfile = "Dockerfile"
  platforms  = ["linux/amd64", "linux/arm64"]

  args = {
    VERSION           = VERSION
    VCS_REF           = VCS_REF
    SOURCE_DATE_EPOCH = SOURCE_DATE_EPOCH
  }

  labels = {
    "org.opencontainers.image.created" = BUILD_DATE
  }

  tags = split(",", TAGS)
}

# Single-arch convenience targets (used by the reproducibility comparison, which
# must build one architecture twice with identical inputs).
target "amd64" {
  inherits  = ["server"]
  platforms = ["linux/amd64"]
}

target "arm64" {
  inherits  = ["server"]
  platforms = ["linux/arm64"]
}
