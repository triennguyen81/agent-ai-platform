"""Skill loader — scan SKILL.md files, parse frontmatter, select for a turn.

Frontmatter is a minimal YAML-ish subset (no PyYAML dep): top-level scalars,
list items prefixed with "- ", and multi-line block scalars via "|".
Anything more exotic should be promoted to PyYAML — for now this stays
dependency-free.
"""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from pathlib import Path

logger = logging.getLogger(__name__)

_FRONTMATTER_RE = re.compile(r"\A---\s*\n(.*?)\n---\s*\n(.*)\Z", re.DOTALL)


@dataclass(frozen=True, slots=True)
class Skill:
    """A skill loaded from disk."""

    name: str
    description: str
    body: str
    keywords: tuple[str, ...] = ()
    always_on: bool = False
    requires_knowledge_context: bool = False

    def matches(self, prompt: str) -> bool:
        if not self.keywords:
            return False
        haystack = prompt.lower()
        return any(kw.lower() in haystack for kw in self.keywords)


@dataclass(frozen=True, slots=True)
class SkillRegistry:
    skills: tuple[Skill, ...] = field(default_factory=tuple)

    def by_name(self, name: str) -> Skill | None:
        return next((s for s in self.skills if s.name == name), None)


def _strip_inline_comment(value: str) -> str:
    # Allow "- foo  # note" style comments in scalar lists.
    return value.split("#", 1)[0].strip().strip('"').strip("'")


def _parse_frontmatter(raw: str) -> dict[str, object]:
    """Parse a tiny subset of YAML: key: scalar, key: list[-], block scalar |."""
    result: dict[str, object] = {}
    lines = raw.split("\n")
    i = 0
    while i < len(lines):
        line = lines[i]
        if not line.strip() or line.lstrip().startswith("#"):
            i += 1
            continue
        match = re.match(r"^([A-Za-z_][A-Za-z0-9_-]*)\s*:\s*(.*)$", line)
        if not match:
            i += 1
            continue
        key, value = match.group(1), match.group(2).strip()
        if value == "|":
            # Block scalar — gather indented lines until indent ends.
            i += 1
            block: list[str] = []
            while i < len(lines) and (lines[i].startswith("  ") or lines[i] == ""):
                block.append(lines[i][2:] if lines[i].startswith("  ") else "")
                i += 1
            result[key] = "\n".join(block).strip()
            continue
        if value == "":
            # List or empty — peek next lines for "- " items.
            items: list[str] = []
            i += 1
            while i < len(lines) and lines[i].lstrip().startswith("- "):
                item = lines[i].lstrip()[2:]
                items.append(_strip_inline_comment(item))
                i += 1
            result[key] = items
            continue
        # Inline scalar
        result[key] = _strip_inline_comment(value)
        i += 1
    return result


def _load_skill(skill_md: Path) -> Skill | None:
    try:
        raw = skill_md.read_text(encoding="utf-8")
    except OSError as exc:
        logger.warning("Cannot read %s: %s", skill_md, exc)
        return None

    match = _FRONTMATTER_RE.match(raw)
    if not match:
        logger.warning("No frontmatter in %s — skipping", skill_md)
        return None

    meta = _parse_frontmatter(match.group(1))
    body = match.group(2).strip()
    name = meta.get("name") or skill_md.parent.name
    description = meta.get("description") or ""
    keywords_raw = meta.get("keywords") or []
    keywords: tuple[str, ...] = (
        tuple(keywords_raw) if isinstance(keywords_raw, list) else ()
    )

    when_raw = meta.get("when") or []
    when_tokens = set(when_raw) if isinstance(when_raw, list) else set()

    always_on = str(meta.get("always_on", "")).lower() == "true"
    requires_kb = "has_knowledge_context" in when_tokens

    return Skill(
        name=str(name),
        description=str(description).strip(),
        body=body,
        keywords=keywords,
        always_on=always_on,
        requires_knowledge_context=requires_kb,
    )


def build_registry(root: Path) -> SkillRegistry:
    """Scan `root/<skill>/SKILL.md` and build an immutable registry."""
    if not root.exists():
        logger.info("Skills root not found: %s — empty registry", root)
        return SkillRegistry()

    skills: list[Skill] = []
    for skill_md in sorted(root.glob("*/SKILL.md")):
        skill = _load_skill(skill_md)
        if skill is None:
            continue
        skills.append(skill)
        logger.info(
            "Loaded skill %s (keywords=%d, always_on=%s, needs_kb=%s)",
            skill.name,
            len(skill.keywords),
            skill.always_on,
            skill.requires_knowledge_context,
        )
    return SkillRegistry(skills=tuple(skills))


def select_skills(
    registry: SkillRegistry,
    *,
    prompt: str,
    has_knowledge_context: bool,
) -> tuple[Skill, ...]:
    """Pick which skills to activate for this turn.

    Selection rules:
    - always_on skills (e.g. vietnamese-style) → always included.
    - requires_knowledge_context skills → only if KB block exists this turn.
    - others → keyword match on the user prompt (case-insensitive substring).
    """
    chosen: list[Skill] = []
    for skill in registry.skills:
        if skill.always_on:
            chosen.append(skill)
            continue
        if skill.requires_knowledge_context:
            if has_knowledge_context:
                chosen.append(skill)
            continue
        if skill.matches(prompt):
            chosen.append(skill)
    return tuple(chosen)
