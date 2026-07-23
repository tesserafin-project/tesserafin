# Buildx bake definition for the reproducible Tesserafin production image (#87).
#
# One commit builds both linux/amd64 and linux/arm64. Version derives from the
# canonical version source (SharedVersion.cs -> passed in as VERSION); the short
# commit SHA is embedded in the pre-release tag. `latest` is intentionally never
# defined here.
#
# All build-affecting values are variables so the wrapper scripts in docker/ can
# derive them deterministically from git (VCS_REF, SOURCE_DATE_EPOCH, BUILD_DATE).
# Reproducible builds require disabling provenance/SBOM attestations, which embed
# build timestamps: pass `--provenance=false --sbom=false` (docker/build-clean.sh
# does this).

variable "VERSION" {
  default = "12.0.0"
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

# Registry/repository for the private pre-release image (no tag pushed as latest).
variable "REGISTRY" {
  default = "ghcr.io/tesserafin-project/tesserafin"
}

function "short" {
  params = [sha]
  result = substr(sha, 0, 12)
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

  tags = [
    "${REGISTRY}:${VERSION}-dev.${short(VCS_REF)}",
    "${REGISTRY}:sha-${VCS_REF}",
  ]
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
