#!/usr/bin/env python3
"""Prune old per-commit GHCR image builds without breaking the images kept.

    GH_TOKEN=... python3 -I ops/ghcr-prune.py --owner OWNER --keep 2 mailvec mailvec-mbsync
    ... --dry-run     print the plan, delete nothing

Run by .github/workflows/cleanup.yml. Standard library only.

Why this is not a tag filter. A published image is an INDEX that points at
untagged children: the platform image and its provenance/SBOM attestation.
GHCR lists every one of them as a separate package version, and only the index
carries tags. The first version of this prune protected "versions tagged v* or
latest" and deleted the rest beyond the newest two, so it deleted the children
of every release older than a week. The tags still resolved and the pull then
failed with "manifest unknown". Four releases were lost that way.

So protection here follows references, not tags:

  roots      every version tagged v* or latest, plus the newest --keep
             per-commit builds (versions carrying only other tags, sha-*)
  protected  the roots and every digest a root's index lists
  deleted    older per-commit builds, and untagged versions that no root
             references and that are older than the grace period

Three rules keep it from repeating the original mistake:

  * It fails closed. If any root's index cannot be read, nothing in that
    package is deleted: an unreadable index is an unknown set of children.
  * Untagged versions younger than the grace period are left alone. A build
    pushes its children before the index that references them, so mid-publish
    they look exactly like orphans.
  * After deleting, every root is re-checked. A child that resolved before the
    run and does not after it fails the run. Roots that were already broken
    are reported as warnings, not failures, so a past loss cannot keep the
    job red.
"""
import argparse
import base64
import datetime
import json
import os
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"
REGISTRY = "https://ghcr.io"
MANIFEST_TYPES = ", ".join([
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])


class Unreadable(Exception):
    """A root's index could not be read, so its children are unknown."""


def is_release(tags):
    return any(t == "latest" or t.startswith("v") for t in tags)


def plan(versions, children_of, keep, now, grace):
    """Decide what to delete. Pure: all I/O is behind `children_of`.

    versions     [{"id", "digest", "created" (aware datetime), "tags" [str]}]
    children_of  digest -> iterable of child digests; raises Unreadable
    Returns (roots, delete) as lists of version dicts.
    """
    releases = [v for v in versions if is_release(v["tags"])]
    builds = sorted((v for v in versions if v["tags"] and not is_release(v["tags"])),
                    key=lambda v: v["created"], reverse=True)
    roots = releases + builds[:keep]

    protected = {v["digest"] for v in roots}
    for root in roots:
        protected.update(children_of(root["digest"]))

    delete = []
    for v in builds[keep:]:
        if v["digest"] not in protected:
            delete.append(v)
    for v in versions:
        if v["tags"] or v["digest"] in protected:
            continue
        if now - v["created"] < grace:
            continue
        delete.append(v)
    return roots, delete


# ── I/O ──────────────────────────────────────────────────────────────────────

def _request(url, headers, method="GET"):
    req = urllib.request.Request(url, headers=headers, method=method)
    return urllib.request.urlopen(req, timeout=60)


def list_versions(owner, package, token):
    """Every version of a user-owned container package, or None if it does not exist."""
    out, page = [], 1
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json"}
    while True:
        url = f"{API}/users/{owner}/packages/container/{package}/versions?per_page=100&page={page}"
        try:
            with _request(url, headers) as resp:
                batch = json.load(resp)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise
        for v in batch:
            out.append({
                "id": v["id"],
                "digest": v["name"],
                "created": datetime.datetime.fromisoformat(v["created_at"].replace("Z", "+00:00")),
                "tags": (v.get("metadata", {}).get("container", {}).get("tags")) or [],
            })
        if len(batch) < 100:
            return out
        page += 1


def delete_version(owner, package, version_id, token):
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json"}
    _request(f"{API}/users/{owner}/packages/container/{package}/versions/{version_id}", headers, "DELETE").close()


def registry_token(owner, package, token):
    """A pull token for the registry: with our credentials if they work, else anonymous (public packages)."""
    url = f"{REGISTRY}/token?scope=repository:{owner}/{package}:pull"
    attempts = []
    if token:
        basic = base64.b64encode(f"{owner}:{token}".encode()).decode()
        attempts.append({"Authorization": f"Basic {basic}"})
    attempts.append({})
    for headers in attempts:
        try:
            with _request(url, headers) as resp:
                return json.load(resp)["token"]
        except (urllib.error.HTTPError, KeyError):
            continue
    raise Unreadable(f"no registry token for {owner}/{package}")


class Registry:
    def __init__(self, owner, package, token):
        self.base = f"{REGISTRY}/v2/{owner}/{package}/manifests/"
        self.headers = {"Authorization": f"Bearer {registry_token(owner, package, token)}",
                        "Accept": MANIFEST_TYPES}

    def children_of(self, digest):
        """Digests an index lists; empty for a plain image. Raises Unreadable if it cannot be read."""
        try:
            with _request(self.base + digest, self.headers) as resp:
                manifest = json.load(resp)
        except (urllib.error.URLError, ValueError) as e:
            raise Unreadable(f"{digest}: {e}") from e
        return [m["digest"] for m in manifest.get("manifests", [])]

    def exists(self, digest):
        try:
            _request(self.base + digest, self.headers, "HEAD").close()
            return True
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return False
            raise


def missing_children(roots, registry):
    """{root digest: [child digests that do not resolve]} for roots with any."""
    broken = {}
    for root in roots:
        try:
            gone = [d for d in registry.children_of(root["digest"]) if not registry.exists(d)]
        except Unreadable:
            gone = ["<index unreadable>"]
        if gone:
            broken[root["digest"]] = gone
    return broken


def label(v):
    return ",".join(v["tags"]) or "untagged"


def prune(owner, package, token, keep, grace, dry_run, now):
    """Returns True if the package is no worse off than before the run."""
    versions = list_versions(owner, package, token)
    if versions is None:
        print(f"{package}: not published; nothing to prune")
        return True

    registry = Registry(owner, package, token)
    try:
        roots, delete = plan(versions, registry.children_of, keep, now, grace)
    except Unreadable as e:
        print(f"::error::{package}: could not read a kept image's index ({e}); deleting nothing")
        return False

    before = missing_children(roots, registry)
    by_digest = {v["digest"]: v for v in versions}
    for digest, gone in before.items():
        print(f"::warning::{package}: {label(by_digest[digest])} was already missing "
              f"{len(gone)} manifest(s) before this run and cannot be pulled")

    print(f"{package}: {len(versions)} versions, {len(roots)} kept images, {len(delete)} to delete")
    for v in delete:
        print(f"  {'would delete' if dry_run else 'deleting'} {v['id']} {v['digest'][7:19]} "
              f"{v['created']:%Y-%m-%d} {label(v)}")
        if not dry_run:
            delete_version(owner, package, v["id"], token)
    if dry_run:
        return True

    after = missing_children(roots, registry)
    newly = {d: g for d, g in after.items() if d not in before or set(g) - set(before[d])}
    for digest, gone in newly.items():
        print(f"::error::{package}: {label(by_digest[digest])} lost {len(gone)} manifest(s) in this run")
    return not newly


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("packages", nargs="+")
    ap.add_argument("--owner", required=True)
    ap.add_argument("--keep", type=int, default=2, help="per-commit builds to keep (default 2)")
    ap.add_argument("--grace-hours", type=float, default=24,
                    help="leave untagged versions younger than this alone (default 24)")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        print("GH_TOKEN is required to list package versions.", file=sys.stderr)
        return 2

    now = datetime.datetime.now(datetime.timezone.utc)
    grace = datetime.timedelta(hours=args.grace_hours)
    results = [prune(args.owner, p, token, args.keep, grace, args.dry_run, now) for p in args.packages]
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
