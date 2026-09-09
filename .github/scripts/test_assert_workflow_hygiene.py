"""Regression tests for the local-action pin coverage in assert_workflow_hygiene.py.

A docker action carries no `uses:` steps, so composite_step_refs alone contributed
nothing for it and its registry image escaped the pin check while the run summary
claimed every container image pinned.
"""
import pytest

import assert_workflow_hygiene as hygiene


def test_composite_step_refs_unchanged():
    document = {
        "runs": {
            "using": "composite",
            "steps": [{"uses": "actions/checkout@v7"}, {"run": "echo hi"}],
        }
    }
    assert hygiene.local_action_refs(document) == ["actions/checkout@v7"]


def test_docker_action_registry_image_is_a_ref():
    document = {"runs": {"using": "docker", "image": "docker://alpine:3.18"}}
    assert hygiene.local_action_refs(document) == ["docker://alpine:3.18"]


@pytest.mark.parametrize("image", ["Dockerfile", "./docker/Dockerfile", "build.dockerfile"])
def test_docker_action_local_build_context_is_not_a_ref(image):
    # A local Dockerfile builds this repository's own reviewed code, like a
    # composite's steps; it has no revision to pin.
    document = {"runs": {"using": "docker", "image": image}}
    assert hygiene.local_action_refs(document) == []


def test_node_action_contributes_no_refs():
    document = {"runs": {"using": "node20", "main": "dist/index.js"}}
    assert hygiene.local_action_refs(document) == []


def write_action(tmp_path, image):
    action_dir = tmp_path / "local-docker"
    action_dir.mkdir()
    (action_dir / "action.yml").write_text(
        f"name: local-docker\nruns:\n  using: docker\n  image: {image}\n",
        encoding="utf-8",
    )
    return "./local-docker"


def test_local_docker_action_with_unpinned_image_fails(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    ref = write_action(tmp_path, "docker://alpine:3.18")
    failed, _ = hygiene.check_local_action("job", ref, "origin.yml", 0, set())
    assert failed


def test_local_docker_action_with_digest_pinned_image_passes(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    ref = write_action(tmp_path, "docker://alpine@sha256:" + "a" * 64)
    failed, _ = hygiene.check_local_action("job", ref, "origin.yml", 0, set())
    assert not failed


def test_local_docker_action_building_own_dockerfile_passes(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    ref = write_action(tmp_path, "Dockerfile")
    failed, _ = hygiene.check_local_action("job", ref, "origin.yml", 0, set())
    assert not failed
