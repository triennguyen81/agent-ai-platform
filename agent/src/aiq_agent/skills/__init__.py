"""Skill system: SKILL.md packages loaded as prompt fragments.

Plain chat path uses these as additional system-prompt material; future
agentic endpoint (Phase 2) will hand them to the Claude Agent SDK directly.
"""

from aiq_agent.skills.loader import Skill, SkillRegistry, build_registry, select_skills

__all__ = ["Skill", "SkillRegistry", "build_registry", "select_skills"]
