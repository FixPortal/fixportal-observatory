"""Regression tests for the local-action pin coverage in assert_workflow_hygiene.py.

A docker action carries no `uses:` steps, so composite_step_refs alone contributed
nothing for it and its registry image escaped the pin check while the run summary
claimed every container image pinned.
"""
import pytest

import assert_workflow_hygiene as hygiene


def test_composite_step_refs_unchanged(tmp_path):
    document = {
        "runs": {
            "using": "composite",
            "steps": [{"uses": "actions/checkout@v7"}, {"run": "echo hi"}],
        }
    }
    assert hygiene.local_action_refs(document, tmp_path) == ["actions/checkout@v7"]


def test_docker_action_registry_image_is_a_ref(tmp_path):
    document = {"runs": {"using": "docker", "image": "docker://alpine:3.18"}}
    assert hygiene.local_action_refs(document, tmp_path) == ["docker://alpine:3.18"]


@pytest.mark.parametrize("image", ["Dockerfile", "./docker/Dockerfile"])
def test_docker_action_local_build_context_is_not_a_ref(image, tmp_path):
    # A local Dockerfile builds this repository's own reviewed code, like a
    # composite's steps; it has no revision to pin.
    document = {"runs": {"using": "docker", "image": image}}
    dockerfile = tmp_path / image
    dockerfile.parent.mkdir(parents=True, exist_ok=True)
    dockerfile.write_text("FROM scratch\n", encoding="utf-8")
    assert hygiene.local_action_refs(document, tmp_path) == []


@pytest.mark.parametrize(
    "image",
    [
        "build.dockerfile", "mydockerfile", "docker://untrusted/dockerfile",
        "myregistry.example.com/Dockerfile", "DockerFile",
    ],
)
def test_docker_action_non_dockerfile_image_is_a_ref(image, tmp_path):
    # A lookalike filename or registry path must still reach check_ref, even on
    # a case-insensitive filesystem with a real Dockerfile beside the action.
    (tmp_path / "Dockerfile").write_text("FROM scratch\n", encoding="utf-8")
    document = {"runs": {"using": "docker", "image": image}}
    assert hygiene.local_action_refs(document, tmp_path) == [image]


def test_node_action_contributes_no_refs(tmp_path):
    document = {"runs": {"using": "node20", "main": "dist/index.js"}}
    assert hygiene.local_action_refs(document, tmp_path) == []


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
    (tmp_path / ref / "Dockerfile").write_text("FROM scratch\n", encoding="utf-8")
    failed, _ = hygiene.check_local_action("job", ref, "origin.yml", 0, set())
    assert not failed
