"""Language detection for Python projects."""

from typing import Any, Dict, List


MANIFEST_NAMES = {
    "pyproject.toml",
    "setup.py",
    "setup.cfg",
    "requirements.txt",
    "Pipfile",
    "poetry.lock",
    "tox.ini",
    "pytest.ini",
}


def detect_python(files: List[Dict[str, Any]]) -> Dict[str, Any]:
    evidence: List[Dict[str, Any]] = []
    python_count = 0

    for file in files:
        path = file.get("path", "").replace("\\", "/")
        filename = path.split("/")[-1]

        if filename in MANIFEST_NAMES:
            evidence.append({"kind": "manifest", "path": path})
        elif path.endswith(".py"):
            python_count += 1

    if python_count > 0:
        evidence.append({"kind": "source", "count": python_count})

    if not evidence:
        return {"language": "python", "confidence": "none", "evidence": []}

    confidence = "high" if python_count > 0 and len(evidence) > 1 else ("medium" if python_count > 0 else "low")
    return {
        "language": "python",
        "confidence": confidence,
        "evidence": evidence,
    }
