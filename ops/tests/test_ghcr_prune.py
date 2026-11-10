"""ops/ghcr-prune.py: what the weekly image prune may and may not delete.

The first prune was a tag filter and deleted the untagged children of every
release image, leaving tags that resolved to nothing pullable. These tests
drive the planning function with the shape GHCR really reports: one tagged
index per image and untagged children that only the index references.
"""
import datetime
import importlib.util
import pathlib
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("ghcr_prune", REPO / "ops" / "ghcr-prune.py")
gp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gp)

NOW = datetime.datetime(2026, 11, 16, 5, 17, tzinfo=datetime.timezone.utc)
GRACE = datetime.timedelta(hours=24)


class Registry:
    """A fake package: images published in order, each an index with two children."""

    def __init__(self):
        self.versions = []
        self.children = {}
        self._n = 0

    def publish(self, days_ago, *tags, hours_ago=0):
        self._n += 1
        created = NOW - datetime.timedelta(days=days_ago, hours=hours_ago)
        index = f"sha256:index{self._n}"
        kids = [f"sha256:image{self._n}", f"sha256:attest{self._n}"]
        for digest in kids:
            self.versions.append({"id": len(self.versions) + 1, "digest": digest, "created": created, "tags": []})
        self.versions.append({"id": len(self.versions) + 1, "digest": index, "created": created, "tags": list(tags)})
        self.children[index] = kids
        return index

    def orphan(self, days_ago, hours_ago=0):
        self._n += 1
        digest = f"sha256:orphan{self._n}"
        created = NOW - datetime.timedelta(days=days_ago, hours=hours_ago)
        self.versions.append({"id": len(self.versions) + 1, "digest": digest, "created": created, "tags": []})
        return digest

    def children_of(self, digest):
        return self.children.get(digest, [])

    def plan(self, keep=2):
        roots, delete = gp.plan(self.versions, self.children_of, keep, NOW, GRACE)
        return {v["digest"] for v in roots}, {v["digest"] for v in delete}


class PlanTests(unittest.TestCase):
    def test_a_release_keeps_its_untagged_children_however_old(self):
        # The bug. A release's children carry no tag, so a tag filter saw
        # them as untagged leftovers and deleted them after a week.
        r = Registry()
        old = r.publish(120, "v0.10.0", "sha-aaa")
        for i in range(5):
            r.publish(10 - i, f"sha-new{i}")

        _, delete = r.plan()

        for digest in [old] + r.children[old]:
            self.assertNotIn(digest, delete)

    def test_latest_is_a_release_too(self):
        r = Registry()
        latest = r.publish(40, "latest")
        for i in range(4):
            r.publish(5 - i, f"sha-{i}")

        _, delete = r.plan()

        self.assertFalse({latest, *r.children[latest]} & delete)

    def test_the_newest_builds_are_kept_whole_and_older_ones_deleted_whole(self):
        # "Keep the newest two" used to count individual manifests, which
        # could keep half of one build. A build is an index and its children.
        r = Registry()
        builds = [r.publish(days, f"sha-{days}") for days in (9, 7, 5, 3)]

        roots, delete = r.plan(keep=2)

        for kept in builds[2:]:
            self.assertIn(kept, roots)
            self.assertFalse({kept, *r.children[kept]} & delete)
        for dropped in builds[:2]:
            self.assertTrue({dropped, *r.children[dropped]} <= delete)

    def test_a_child_shared_with_a_kept_image_survives_the_old_build_going(self):
        # An unchanged layer set rebuilds to the same child digest, so one
        # version can sit under an old build and a release at once.
        r = Registry()
        old = r.publish(30, "sha-old")
        release = r.publish(20, "v1.0.0")
        r.children[release] = r.children[release] + [r.children[old][0]]
        r.publish(3, "sha-a")
        r.publish(2, "sha-b")

        _, delete = r.plan()

        self.assertIn(old, delete)
        self.assertNotIn(r.children[old][0], delete)
        self.assertIn(r.children[old][1], delete)

    def test_an_unreferenced_untagged_version_is_deleted_once_past_the_grace_period(self):
        r = Registry()
        r.publish(3, "sha-a")
        stale = r.orphan(days_ago=2)

        _, delete = r.plan()

        self.assertIn(stale, delete)

    def test_a_fresh_untagged_version_is_left_alone(self):
        # A build pushes its children before the index that references them.
        # Mid-publish they are indistinguishable from orphans.
        r = Registry()
        r.publish(3, "sha-a")
        in_flight = r.orphan(days_ago=0, hours_ago=1)

        _, delete = r.plan()

        self.assertNotIn(in_flight, delete)

    def test_an_unreadable_index_stops_the_plan_instead_of_guessing(self):
        # An index we cannot read has an unknown set of children. Deleting
        # "everything unreferenced" at that point is the original bug again.
        r = Registry()
        release = r.publish(60, "v1.0.0")
        r.publish(2, "sha-a")

        def children_of(digest):
            if digest == release:
                raise gp.Unreadable("registry said 500")
            return r.children_of(digest)

        with self.assertRaises(gp.Unreadable):
            gp.plan(r.versions, children_of, 2, NOW, GRACE)

    def test_a_plain_image_with_no_index_is_just_itself(self):
        r = Registry()
        r.versions.append({"id": 99, "digest": "sha256:plain", "created": NOW - datetime.timedelta(days=90),
                           "tags": ["v0.1.0"]})

        roots, delete = r.plan()

        self.assertIn("sha256:plain", roots)
        self.assertNotIn("sha256:plain", delete)

    def test_nothing_is_deleted_when_there_is_nothing_old(self):
        r = Registry()
        r.publish(2, "v1.0.0", "latest", "sha-a")
        r.publish(1, "sha-b")

        _, delete = r.plan()

        self.assertEqual(delete, set())


class BreakageTests(unittest.TestCase):
    class FakeRegistry:
        def __init__(self, children, present):
            self.children, self.present = children, present

        def children_of(self, digest):
            if digest not in self.children:
                raise gp.Unreadable(digest)
            return self.children[digest]

        def exists(self, digest):
            return digest in self.present

    def test_a_root_with_a_missing_child_is_reported(self):
        roots = [{"digest": "sha256:ok"}, {"digest": "sha256:broken"}]
        registry = self.FakeRegistry(
            {"sha256:ok": ["sha256:a"], "sha256:broken": ["sha256:b", "sha256:c"]},
            present={"sha256:a", "sha256:b"})

        self.assertEqual(gp.missing_children(roots, registry), {"sha256:broken": ["sha256:c"]})

    def test_a_root_whose_index_is_gone_counts_as_broken(self):
        registry = self.FakeRegistry({}, present=set())

        self.assertIn("sha256:gone", gp.missing_children([{"digest": "sha256:gone"}], registry))


class DeadReleaseTests(unittest.TestCase):
    """--delete-dead-releases is the one path that deletes a v* image, so what counts as dead is narrow."""

    KIDS = ["sha256:a", "sha256:b"]

    def test_a_release_with_every_child_gone_is_dead(self):
        self.assertTrue(gp.is_dead_release(["v0.9.0", "sha-abc"], self.KIDS, self.KIDS))

    def test_a_release_with_one_child_left_is_not(self):
        # Damaged some other way. That wants a look, not a delete.
        self.assertFalse(gp.is_dead_release(["v0.9.0"], self.KIDS, ["sha256:a"]))

    def test_an_intact_release_is_not(self):
        self.assertFalse(gp.is_dead_release(["v0.12.1"], self.KIDS, []))

    def test_latest_is_never_dead_whatever_else_it_carries(self):
        self.assertFalse(gp.is_dead_release(["latest"], self.KIDS, self.KIDS))
        self.assertFalse(gp.is_dead_release(["v1.0.0", "latest"], self.KIDS, self.KIDS))

    def test_a_per_commit_build_is_not_a_release(self):
        self.assertFalse(gp.is_dead_release(["sha-abc"], self.KIDS, self.KIDS))

    def test_an_image_with_no_children_listed_is_not_dead(self):
        # A plain image, or an index we read as empty: nothing proves it broken.
        self.assertFalse(gp.is_dead_release(["v0.1.0"], [], []))

    def test_an_index_that_cannot_be_read_is_skipped_not_deleted(self):
        registry = BreakageTests.FakeRegistry({}, present=set())
        roots = [{"digest": "sha256:unreadable", "tags": ["v0.5.0"]}]

        self.assertEqual(gp.dead_releases(roots, registry), [])

    def test_only_the_fully_dead_releases_are_selected(self):
        registry = BreakageTests.FakeRegistry(
            {"sha256:dead": ["sha256:x", "sha256:y"], "sha256:live": ["sha256:p", "sha256:q"]},
            present={"sha256:p", "sha256:q"})
        roots = [{"digest": "sha256:dead", "tags": ["v0.9.0"]}, {"digest": "sha256:live", "tags": ["v0.12.1"]}]

        self.assertEqual([r["digest"] for r in gp.dead_releases(roots, registry)], ["sha256:dead"])


if __name__ == "__main__":
    unittest.main()
